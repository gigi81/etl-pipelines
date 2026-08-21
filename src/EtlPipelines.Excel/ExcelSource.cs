using System.IO.Abstractions;
using MiniExcelLib;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Excel;

/// <summary>
/// Reads rows from a worksheet in an Excel (<c>.xlsx</c>) workbook.
/// </summary>
/// <remarks>
/// <para>
/// The file is opened during <see cref="InitializeAsync"/> rather than at construction, so a source
/// registered in the container holds no file handle between runs. Combined with the scoped
/// registration the builder applies, each run opens the workbook itself and the run's scope closes it.
/// </para>
/// <para>
/// Rows are pulled one at a time from MiniExcel's asynchronous stream, so a workbook is never
/// materialised: the sheet is read as the pipeline consumes it, and abandoning a run stops the read
/// rather than finishing it.
/// </para>
/// <para>
/// The file is an <see cref="IFileInfo"/> rather than a path, so the source never has to be told which
/// filesystem to use — the file already knows, through <see cref="IFileSystemInfo.FileSystem"/>. That
/// is also what lets the whole source run against an in-memory filesystem in a test.
/// </para>
/// </remarks>
/// <typeparam name="TRow">
/// The row type to read into. It needs a parameterless constructor and settable properties, which is
/// how a worksheet's columns are matched to it by name.
/// </typeparam>
public sealed class ExcelSource<TRow> : IDataSource<TRow>, IAsyncInitializable
    where TRow : class, new()
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly ExcelSourceOptions _options;
    private readonly IDeadLetterSink<string>? _deadLetters;
    private readonly bool _ownsStream;

    private Stream? _stream;
    private IAsyncEnumerator<object>? _rows;
    private bool _exhausted;

    /// <summary>Reads <paramref name="file"/>.</summary>
    /// <param name="file">The workbook to read. Its filesystem is the one the source reads through.</param>
    /// <param name="options">Which sheet to read and how to treat rows that will not convert.</param>
    /// <param name="deadLetters">Receives the cell values of any row that could not be converted.</param>
    public ExcelSource(
        IFileInfo file,
        ExcelSourceOptions? options = null,
        IDeadLetterSink<string>? deadLetters = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        _options = options ?? new ExcelSourceOptions();
        _deadLetters = deadLetters;
        _ownsStream = true;
        _open = _ => new ValueTask<Stream>(file.OpenRead());
    }

    /// <summary>Reads a workbook from a stream opened when the run starts.</summary>
    /// <param name="open">Opens the stream. Called once per run.</param>
    /// <param name="options">Which sheet to read and how to treat rows that will not convert.</param>
    /// <param name="deadLetters">Receives the cell values of any row that could not be converted.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the stream.</param>
    public ExcelSource(
        Func<CancellationToken, ValueTask<Stream>> open,
        ExcelSourceOptions? options = null,
        IDeadLetterSink<string>? deadLetters = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
        _options = options ?? new ExcelSourceOptions();
        _deadLetters = deadLetters;
        _ownsStream = !leaveOpen;
    }

    /// <summary>
    /// Rows skipped because they could not be converted to <typeparamref name="TRow"/>.
    /// </summary>
    /// <remarks>
    /// Tracked here because the source port has nowhere to report it. A transform can reject an
    /// individual row — <c>TransformResult.RejectedRow</c> carries it into the configured
    /// <see cref="RowErrorAction"/>, the <c>MaxRowErrors</c> budget and the dead-letter sink — but
    /// <see cref="IDataSource{TRow}.ReadAsync"/> returns only a count, with no channel for a rejected
    /// row. So these skips do <b>not</b> appear in the run's <c>RowsFailed</c> and do not count
    /// against <c>MaxRowErrors</c>; check this property, and the dead-letter sink, to see them.
    /// </remarks>
    public long MalformedRows { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_rows is not null)
        {
            return;
        }

        _stream = await _open(cancellationToken).ConfigureAwait(false);

        // The untyped stream, on purpose: it yields the cells as they are, leaving conversion to
        // happen one row at a time where a failure can be contained. See ExcelRowMapper.
        var rows = MiniExcel.Importers.GetOpenXmlImporter().QueryAsync(
            _stream,
            hasHeaderRow: _options.HasHeaderRow,
            sheetName: _options.SheetName,
            startCell: _options.StartCell,
            configuration: _options.CreateConfiguration(),
            leaveOpen: true,
            cancellationToken: cancellationToken);

        _rows = rows.GetAsyncEnumerator(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_rows is null)
        {
            return Error.Failure(
                "excel.not_initialized",
                $"{nameof(ExcelSource<TRow>)} has no open workbook. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first read.");
        }

        if (_exhausted)
        {
            return 0;
        }

        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await _rows.MoveNextAsync().ConfigureAwait(false))
            {
                _exhausted = true;
                break;
            }

            if (_rows.Current is not IDictionary<string, object?> cells)
            {
                continue;
            }

            var mapped = ExcelRowMapper<TRow>.Map(cells, _options.Culture);
            if (mapped.IsError)
            {
                if (!_options.SkipMalformedRows)
                {
                    return mapped.Errors;
                }

                await RejectAsync(cells, mapped.FirstError, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // buffer.Span is re-acquired per row on purpose: a Span cannot live across an await.
            buffer.Span[count++] = mapped.Value;
        }

        return count;
    }

    /// <summary>Records a row that would not convert and hands its cells on for recovery.</summary>
    private async ValueTask RejectAsync(
        IDictionary<string, object?> cells,
        Error error,
        CancellationToken cancellationToken)
    {
        MalformedRows++;

        if (_deadLetters is null)
        {
            return;
        }

        // The cell values are what make the row recoverable — a converted shape is exactly what is
        // missing.
        await _deadLetters
            .WriteAsync(ExcelRowMapper<TRow>.Describe(cells), error, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_rows is not null)
        {
            await _rows.DisposeAsync().ConfigureAwait(false);
            _rows = null;
        }

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
