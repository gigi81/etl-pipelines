using System.IO.Abstractions;
using System.Threading.Channels;

namespace EtlPipelines.Extensions.Excel;

/// <summary>
/// Writes rows to one sheet of a workbook that several stages are building together.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="ExcelSink{TRow}"/> for a workbook with more than one sheet. Each
/// stage owns a sheet; the workbook itself is shared for the length of the run and renamed into place
/// once the last sheet is written, so the target either does not exist or holds every sheet.
/// </remarks>
/// <typeparam name="TRow">The row type this sheet is written from.</typeparam>
public sealed class ExcelSheetSink<TRow> : IDataSink<TRow>, IAsyncInitializable, IAsyncCompletable
{
    private readonly IServiceProvider _run;
    private readonly IFileInfo _file;
    private readonly string _sheetName;
    private readonly ExcelSinkOptions _options;

    private ExcelWorkbookWriter? _workbook;
    private SheetWriter<TRow>? _sheet;
    private bool _completed;

    internal ExcelSheetSink(
        IServiceProvider run,
        IFileInfo file,
        string sheetName,
        ExcelSinkOptions options)
    {
        _run = run;
        _file = file;
        _sheetName = sheetName;
        _options = options;
    }

    /// <summary>The file currently being written, temporary until the last sheet promotes it.</summary>
    public IFileInfo? WritingTo => _workbook?.WritingTo;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_sheet is not null)
        {
            return;
        }

        _workbook = Workbooks.For(_run, _file, SheetPlans.For(_file), _options);
        _sheet = await _workbook
            .BeginSheetAsync<TRow>(_sheetName, _options.WriteHeader, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (_sheet is null)
        {
            return Error.Failure(
                "excel.not_initialized",
                $"{nameof(ExcelSheetSink<TRow>)} has no open sheet. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first write.");
        }

        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Published straight through, so the pooled batch is never retained past this call.
                await _sheet.Rows.WriteAsync(batch.Span[i], cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return await FaultAsync().ConfigureAwait(false);
            }
        }

        return batch.Length;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_sheet is null || _completed)
        {
            return Result.Success;
        }

        try
        {
            // Completing the last sheet is also what promotes the workbook.
            await _sheet.CompleteAsync().ConfigureAwait(false);
            _completed = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Error.Failure("excel.write_failed", exception.Message);
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Never promotes. A sheet reaching disposal without having completed belongs to a run that
        // failed, and the workbook it was part of must not appear at the target path.
        if (_sheet is not null && !_completed)
        {
            await _sheet.AbandonAsync().ConfigureAwait(false);
        }

        _sheet = null;
    }

    private async ValueTask<Error> FaultAsync()
    {
        try
        {
            // Only reached from WriteAsync, past its own "_sheet is null" guard - true, but not
            // something the compiler's nullable analysis carries across the call, so it still flags
            // a removal here, and the compiler wins the disagreement with S8969.
#pragma warning disable S8969
            await _sheet!.Writing.ConfigureAwait(false);
#pragma warning restore S8969
        }
        catch (Exception exception)
        {
            return Error.Failure("excel.write_failed", exception.Message);
        }

        return Error.Failure(
            "excel.write_failed",
            "The workbook writer stopped before every row of this sheet had been written.");
    }
}
