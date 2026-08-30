using System.IO.Abstractions;
using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Reads rows from a JSON file, either JSON Lines or a single JSON array. See
/// <see cref="JsonOptions.Format"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stream is opened during <see cref="InitializeAsync"/> rather than at construction, so a
/// source registered in the container holds no file handle between runs. Combined with the scoped
/// registration the builder applies, each run opens the file itself and the run's scope closes it.
/// </para>
/// <para>
/// The file is an <see cref="IFileInfo"/> rather than a path, so the source never has to be told
/// which filesystem to use - the file already knows, through <see cref="IFileSystemInfo.FileSystem"/>.
/// That is also what lets the whole source run against an in-memory filesystem in a test.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
public sealed class JsonSource<TRow> : IDataSource<TRow>, IAsyncInitializable
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly JsonSourceOptions _options;
    private readonly IDeadLetterSink<string>? _deadLetters;
    private readonly bool _ownsStream;

    private Stream? _stream;
    private StreamReader? _lines;
    private IAsyncEnumerator<TRow?>? _array;
    private bool _exhausted;

    /// <summary>Reads <paramref name="file"/>.</summary>
    /// <param name="file">The file to read. Its filesystem is the one the source reads through.</param>
    /// <param name="options">Format settings. Defaults are JSON Lines, camelCase, invariant.</param>
    /// <param name="deadLetters">
    /// Receives the raw text of any line that could not be parsed. Only ever written to under
    /// <see cref="JsonFormat.Lines"/> - see <see cref="JsonSourceOptions.SkipMalformedRows"/>.
    /// </param>
    public JsonSource(
        IFileInfo file,
        JsonSourceOptions? options = null,
        IDeadLetterSink<string>? deadLetters = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        _options = options ?? new JsonSourceOptions();
        _deadLetters = deadLetters;
        _ownsStream = true;
        _open = _ => new ValueTask<Stream>(file.OpenRead());
    }

    /// <summary>Reads JSON from a stream opened when the run starts.</summary>
    /// <param name="open">Opens the stream. Called once per run.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="deadLetters">Receives the raw text of any line that could not be parsed.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the stream.</param>
    public JsonSource(
        Func<CancellationToken, ValueTask<Stream>> open,
        JsonSourceOptions? options = null,
        IDeadLetterSink<string>? deadLetters = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
        _options = options ?? new JsonSourceOptions();
        _deadLetters = deadLetters;
        _ownsStream = !leaveOpen;
    }

    /// <summary>
    /// Lines skipped because they could not be parsed. Always zero under <see cref="JsonFormat.Array"/>,
    /// since a malformed element there fails the read instead of being skipped.
    /// </summary>
    /// <remarks>
    /// Tracked here because the source port has nowhere to report it. A transform can reject an
    /// individual row - <c>TransformResult.RejectedRow</c> carries it into the configured
    /// <see cref="RowErrorAction"/>, the <c>MaxRowErrors</c> budget and the dead-letter sink - but
    /// <see cref="IDataSource{TRow}.ReadAsync"/> returns only a count, with no channel for a rejected
    /// row. So these skips do <b>not</b> appear in the run's <c>RowsFailed</c> and do not count
    /// against <c>MaxRowErrors</c>; check this property, and the dead-letter sink, to see them.
    /// </remarks>
    public long MalformedRows { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        _stream = await _open(cancellationToken).ConfigureAwait(false);

        if (_options.Format == JsonFormat.Lines)
        {
            // leaveOpen: true because this source, not the reader, owns the underlying stream -
            // disposal is decided by _ownsStream in DisposeAsync.
            _lines = new StreamReader(_stream, leaveOpen: true);
        }
        else
        {
            _array = JsonSerializer
                .DeserializeAsyncEnumerable<TRow>(_stream, _options.SerializerOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return Error.Failure(
                "json.not_initialized",
                $"{nameof(JsonSource<TRow>)} has no open stream. It is opened during InitializeAsync, " +
                "which the pipeline calls before the first read.");
        }

        if (_exhausted)
        {
            return 0;
        }

        return _options.Format == JsonFormat.Lines
            ? await ReadLinesAsync(buffer, cancellationToken).ConfigureAwait(false)
            : await ReadArrayAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ErrorOr<int>> ReadLinesAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await _lines!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                _exhausted = true;
                break;
            }

            // Blank lines are common in hand-edited or hand-concatenated NDJSON; they are not
            // malformed records, just nothing to skip past.
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            TRow? record;
            try
            {
                record = JsonSerializer.Deserialize<TRow>(line, _options.SerializerOptions);
            }
            catch (JsonException ex) when (_options.SkipMalformedRows)
            {
                await RejectAsync(line, ex, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // buffer.Span is re-acquired per row on purpose: a Span cannot live across an await.
            buffer.Span[count++] = record!;
        }

        return count;
    }

    private async ValueTask<ErrorOr<int>> ReadArrayAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool moved;
            try
            {
                moved = await _array!.MoveNextAsync().ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // Unlike ReadLinesAsync, there is no line to resynchronise on: the underlying
                // Utf8JsonReader's position inside the array is now unrecoverable, so the read fails
                // outright rather than skipping one element and continuing.
                return Error.Validation(
                    "json.malformed_array",
                    $"The JSON array could not be parsed: {ex.Message}");
            }

            if (!moved)
            {
                _exhausted = true;
                break;
            }

            buffer.Span[count++] = _array!.Current!;
        }

        return count;
    }

    /// <summary>Records a line that could not be parsed and hands its raw text on for recovery.</summary>
    private async ValueTask RejectAsync(string rawLine, JsonException exception, CancellationToken cancellationToken)
    {
        MalformedRows++;

        if (_deadLetters is null)
        {
            return;
        }

        var error = Error.Validation("json.malformed_row", $"Line could not be parsed: {exception.Message}");
        await _deadLetters.WriteAsync(rawLine, error, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_array is not null)
        {
            await _array.DisposeAsync().ConfigureAwait(false);
            _array = null;
        }

        _lines?.Dispose();
        _lines = null;

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
