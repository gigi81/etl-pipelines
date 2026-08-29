using System.IO.Abstractions;
using System.Threading.Channels;
using MiniExcelLib;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Extensions.Excel;

/// <summary>
/// Writes rows to a worksheet in an Excel (<c>.xlsx</c>) workbook.
/// </summary>
/// <remarks>
/// <para>
/// MiniExcel writes a sheet from a single sequence, pulling rows as it goes, while a sink is handed
/// one batch at a time. The two are joined by a bounded channel: the export runs as a background task
/// reading from the channel, and <see cref="WriteAsync"/> publishes into it. That keeps the workbook
/// streaming — rows reach the file as they arrive rather than being collected first — and the bound is
/// what stops a fast pipeline from queueing an entire load in memory ahead of a slow disk.
/// </para>
/// <para>
/// By default the rows stream into a temporary file beside the target, which is renamed into place
/// from <see cref="CompleteAsync"/> — the hook the runtime calls exactly once, and only when the run
/// succeeded. This matters more for a workbook than for a CSV: an <c>.xlsx</c> is a zip archive whose
/// central directory is written last, so a half-written one does not open at all.
/// </para>
/// <para>
/// The destination is an <see cref="IFileInfo"/> rather than a path, so the sink never has to be told
/// which filesystem to use — the file already knows, through <see cref="IFileSystemInfo.FileSystem"/>.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to write.</typeparam>
public sealed class ExcelSink<TRow> : IDataSink<TRow>, IAsyncInitializable, IAsyncCompletable
{
    private readonly IFileInfo? _target;
    private readonly Stream? _given;
    private readonly ExcelSinkOptions _options;
    private readonly bool _ownsStream;

    private Stream? _stream;
    private IFileInfo? _writingTo;
    private Channel<TRow>? _channel;
    private Task<int[]>? _export;
    private CancellationTokenSource? _cancellation;

    /// <summary>Writes to <paramref name="file"/>, overwriting it if it already exists.</summary>
    /// <param name="file">Destination workbook. Its filesystem is the one the sink writes through.</param>
    /// <param name="options">Sheet settings, including whether to write atomically.</param>
    public ExcelSink(IFileInfo file, ExcelSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        _target = file;
        _options = options ?? new ExcelSinkOptions();
        _ownsStream = true;
    }

    /// <summary>Writes to an existing stream.</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="options">Sheet settings.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the stream.</param>
    /// <remarks>
    /// There is no file to rename here, so the atomic behaviour does not apply: the workbook is built
    /// directly into the stream, and a failed run leaves whatever had been written there.
    /// </remarks>
    public ExcelSink(Stream stream, ExcelSinkOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _given = stream;
        _options = options ?? new ExcelSinkOptions();
        _ownsStream = !leaveOpen;
    }

    /// <summary>
    /// The file currently being written — the temporary one while a run is in flight, and the target
    /// once it has been promoted.
    /// </summary>
    public IFileInfo? WritingTo => _writingTo;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_export is not null)
        {
            return ValueTask.CompletedTask;
        }

        if (_target is not null)
        {
            // The temporary file is a sibling of the target on purpose: a rename is only atomic within
            // a volume, and the system temp directory is often on a different one.
            _writingTo = _options.WriteAtomically
                ? _target.Directory.File($"{Guid.NewGuid():N}.tmp")
                : _target;

            _writingTo.Directory?.Create();
            _stream = _writingTo.Create();
        }
        else
        {
            _stream = _given;
        }

        _channel = Channel.CreateBounded<TRow>(new BoundedChannelOptions(_options.BufferedRows)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _export = MiniExcel.Exporters.GetOpenXmlExporter().ExportAsync(
            _stream!,
            _channel.Reader.ReadAllAsync(_cancellation.Token),
            printHeader: _options.WriteHeader,
            sheetName: _options.SheetName ?? "Sheet1",
            configuration: _options.CreateConfiguration(),
            cancellationToken: _cancellation.Token);

        // Without this, an export that fails leaves nobody reading the channel, and the next write
        // that finds the buffer full would wait for a reader that is never coming back.
        _ = _export.ContinueWith(
            task => _channel.Writer.TryComplete(task.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (_channel is null || _export is null)
        {
            return Error.Failure(
                "excel.not_initialized",
                $"{nameof(ExcelSink<TRow>)} has no open workbook. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first write.");
        }

        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Published straight through, so the pooled batch is never retained past this call.
                await _channel.Writer.WriteAsync(batch.Span[i], cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // The channel is only closed early when the export failed, so that is the error worth
                // reporting rather than the closure it caused.
                return await FaultAsync().ConfigureAwait(false);
            }
        }

        return batch.Length;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_export is null)
        {
            return Result.Success;
        }

        _channel?.Writer.TryComplete();

        try
        {
            await _export.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error.Failure("excel.write_failed", exception.Message);
        }

        // Closed before the rename, not after: an open handle makes the move fail on Windows, and
        // renaming a workbook whose zip directory has not been written would promote one that cannot
        // be opened.
        await CloseAsync().ConfigureAwait(false);

        if (_target is not null && _options.WriteAtomically && _writingTo is not null)
        {
            _writingTo.MoveTo(_target.FullName, overwrite: true);
            _writingTo = _target;
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Never promotes. Disposal runs on the failure path too, so a temporary file reaching this
        // point without having been completed is exactly the one that must not become the target.
        if (_channel is not null)
        {
            _channel.Writer.TryComplete();
        }

        if (_export is not null)
        {
            // The export is reading a channel that may never be completed by a run that gave up, so
            // it is cancelled rather than awaited to a finish.
            await _cancellation!.CancelAsync().ConfigureAwait(false);

            try
            {
                await _export.ConfigureAwait(false);
            }
            catch
            {
                // Disposal reports nothing; the run's own error is the one that matters.
            }

            _export = null;
        }

        _cancellation?.Dispose();
        _cancellation = null;
        _channel = null;

        await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Surfaces the reason the export gave up.</summary>
    private async ValueTask<Error> FaultAsync()
    {
        try
        {
            await _export!.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Error.Failure("excel.write_failed", exception.Message);
        }

        return Error.Failure(
            "excel.write_failed",
            "The workbook writer stopped before every row had been written.");
    }

    private async ValueTask CloseAsync()
    {
        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
