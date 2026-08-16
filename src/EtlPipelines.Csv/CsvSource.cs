using System.IO.Abstractions;
using CsvHelper;

namespace EtlPipelines.Csv;

/// <summary>
/// Reads rows from a CSV file.
/// </summary>
/// <remarks>
/// <para>
/// The file is opened during <see cref="InitializeAsync"/> rather than at construction, so a source
/// registered in the container holds no file handle between runs. Combined with the scoped
/// registration the builder applies, each run opens the file itself and the run's scope closes it.
/// </para>
/// <para>
/// Rows are materialised one object at a time by <c>GetRecord&lt;TRow&gt;()</c>. CsvHelper also
/// offers <c>EnumerateRecords</c>, which reuses a single instance for speed — that must never be used
/// here, because a batch built from it would hold N references to one object showing the last row
/// read.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
public sealed class CsvSource<TRow> : IDataSource<TRow>, IAsyncInitializable
{
    private readonly Func<CancellationToken, ValueTask<TextReader>> _open;
    private readonly CsvSourceOptions _options;
    private readonly IDeadLetterSink<string>? _deadLetters;
    private readonly bool _ownsReader;

    private TextReader? _reader;
    private CsvReader? _csv;
    private bool _exhausted;

    /// <summary>Reads the CSV file at <paramref name="path"/>.</summary>
    /// <param name="path">Path to the file to read.</param>
    /// <param name="options">Format settings. Defaults are UTF-8, comma-delimited, invariant culture.</param>
    /// <param name="deadLetters">Receives the raw text of any row that could not be parsed.</param>
    /// <param name="fileSystem">
    /// The filesystem to read through. Defaults to the real one; pass a <c>MockFileSystem</c> to test
    /// against an in-memory filesystem instead.
    /// </param>
    public CsvSource(
        string path,
        CsvSourceOptions? options = null,
        IDeadLetterSink<string>? deadLetters = null,
        IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var files = fileSystem ?? new FileSystem();

        _options = options ?? new CsvSourceOptions();
        _deadLetters = deadLetters;
        _ownsReader = true;
        _open = _ => new ValueTask<TextReader>(
            new StreamReader(files.FileInfo.New(path).OpenRead(), _options.Encoding));
    }

    /// <summary>Reads CSV from a reader opened when the run starts.</summary>
    /// <param name="open">Opens the reader. Called once per run.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="deadLetters">Receives the raw text of any row that could not be parsed.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the reader.</param>
    public CsvSource(
        Func<CancellationToken, ValueTask<TextReader>> open,
        CsvSourceOptions? options = null,
        IDeadLetterSink<string>? deadLetters = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
        _options = options ?? new CsvSourceOptions();
        _deadLetters = deadLetters;
        _ownsReader = !leaveOpen;
    }

    /// <summary>
    /// Rows skipped because they could not be parsed.
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
        if (_csv is not null)
        {
            return;
        }

        _reader = await _open(cancellationToken).ConfigureAwait(false);
        _csv = new CsvReader(_reader, _options.CreateConfiguration());
        _options.ConfigureContext?.Invoke(_csv.Context);

        if (_options.HasHeaderRecord)
        {
            if (await _csv.ReadAsync().ConfigureAwait(false))
            {
                _csv.ReadHeader();
            }
            else
            {
                // An empty file is not an error; it simply yields nothing.
                _exhausted = true;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_csv is null)
        {
            return Error.Failure(
                "csv.not_initialized",
                $"{nameof(CsvSource<TRow>)} has no open file. It is opened during InitializeAsync, " +
                "which the pipeline calls before the first read.");
        }

        if (_exhausted)
        {
            return 0;
        }

        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await _csv.ReadAsync().ConfigureAwait(false))
            {
                _exhausted = true;
                break;
            }

            TRow record;
            try
            {
                record = _csv.GetRecord<TRow>();
            }
            catch (CsvHelperException ex) when (_options.SkipMalformedRows)
            {
                await RejectAsync(ex, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // buffer.Span is re-acquired per row on purpose: a Span cannot live across an await.
            buffer.Span[count++] = record;
        }

        return count;
    }

    /// <summary>Records a row that could not be parsed and hands its raw text on for recovery.</summary>
    private async ValueTask RejectAsync(CsvHelperException exception, CancellationToken cancellationToken)
    {
        MalformedRows++;

        if (_deadLetters is null)
        {
            return;
        }

        // The raw text is what makes the row recoverable — a parsed shape is exactly what is missing.
        var raw = _csv!.Parser.RawRecord.ToString();
        var error = Error.Validation(
            "csv.malformed_row",
            $"Row {_csv.Parser.Row} could not be parsed: {exception.Message}");

        await _deadLetters.WriteAsync(raw, error, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Synchronous on purpose: CsvReader and StreamReader are IDisposable only — unlike the
        // writer side, which does offer DisposeAsync.
        _csv?.Dispose();
        _csv = null;

        if (_ownsReader)
        {
            _reader?.Dispose();
        }

        _reader = null;
        return ValueTask.CompletedTask;
    }
}
