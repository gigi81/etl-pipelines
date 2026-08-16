using System.IO.Abstractions;
using CsvHelper;

namespace EtlPipelines.Csv;

/// <summary>
/// Writes rows to a CSV file.
/// </summary>
/// <remarks>
/// <para>
/// By default the rows stream into a temporary file beside the target, which is renamed into place
/// from <see cref="CompleteAsync"/> — the hook the runtime calls exactly once, and only when the run
/// succeeded. The target path therefore either does not exist or holds a whole file; a downstream job
/// can never pick up a truncated one. A failed run leaves the temporary file behind for inspection.
/// </para>
/// <para>
/// This is the reason <see cref="IAsyncCompletable"/> exists separately from
/// <see cref="IAsyncDisposable"/>: disposal also runs on the failure path, where promoting a
/// half-written file is precisely the wrong thing to do.
/// </para>
/// <para>
/// The destination is an <see cref="IFileInfo"/> rather than a path, so the sink never has to be told
/// which filesystem to use — the file already knows, through <see cref="IFileSystemInfo.FileSystem"/>.
/// That is also what lets the whole sink run against an in-memory filesystem in a test.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to write.</typeparam>
public sealed class CsvSink<TRow> : IDataSink<TRow>, IAsyncInitializable, IAsyncCompletable
{
    private readonly IFileInfo? _target;
    private readonly CsvSinkOptions _options;
    private readonly bool _ownsWriter;

    private TextWriter? _writer;
    private CsvWriter? _csv;
    private IFileInfo? _writingTo;

    /// <summary>Writes to <paramref name="file"/>, overwriting it if it already exists.</summary>
    /// <param name="file">Destination file. Its filesystem is the one the sink writes through.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    public CsvSink(IFileInfo file, CsvSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        _target = file;
        _options = options ?? new CsvSinkOptions();
        _ownsWriter = true;
    }

    /// <summary>
    /// Writes to an existing <see cref="TextWriter"/>.
    /// </summary>
    /// <param name="writer">Destination writer.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the writer.</param>
    /// <remarks>
    /// There is no file to rename here, so the atomic behaviour does not apply: rows reach the writer
    /// as they are produced, and completion only flushes. A caller who needs all-or-nothing must
    /// arrange it themselves, or use the file-based constructor.
    /// </remarks>
    public CsvSink(TextWriter writer, CsvSinkOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _writer = writer;
        _options = options ?? new CsvSinkOptions();
        _ownsWriter = !leaveOpen;
    }

    /// <summary>
    /// The file currently being written — the temporary one while a run is in flight, and the target
    /// once it has been promoted.
    /// </summary>
    public IFileInfo? WritingTo => _writingTo;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_csv is not null)
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

            // Reads better than pulling the directory name out of the path by hand, and Create is a
            // no-op when the directory is already there.
            _writingTo.Directory?.Create();

            _writer = new StreamWriter(_writingTo.Create(), _options.Encoding);
        }

        _csv = new CsvWriter(_writer!, _options.CreateConfiguration());
        _options.ConfigureContext?.Invoke(_csv.Context);

        if (_options.HasHeaderRecord)
        {
            _csv.WriteHeader<TRow>();
            _csv.NextRecord();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (_csv is null)
        {
            return Error.Failure(
                "csv.not_initialized",
                $"{nameof(CsvSink<TRow>)} has no open writer. It is opened during InitializeAsync, " +
                "which the pipeline calls before the first write.");
        }

        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Written straight through, so the pooled batch is never retained past this call.
            _csv.WriteRecord(batch.Span[i]);
            await _csv.NextRecordAsync().ConfigureAwait(false);
        }

        return batch.Length;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_csv is null)
        {
            return Result.Success;
        }

        await _csv.FlushAsync().ConfigureAwait(false);

        // Closed before the rename, not after: an open handle makes the move fail on Windows, and
        // renaming a file that has not finished flushing would promote a partial one anywhere.
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
        await CloseAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseAsync()
    {
        if (_csv is not null)
        {
            await _csv.DisposeAsync().ConfigureAwait(false);
            _csv = null;
        }

        if (_ownsWriter && _writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        _writer = null;
    }
}
