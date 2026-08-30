using System.Buffers;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Reads rows from a single JSON array holding every row - the array at the document's root by
/// default, or nested under <see cref="JsonArraySourceOptions.Path"/>.
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
/// <para>
/// Rows still arrive one at a time rather than the array being buffered whole in memory - see
/// <see cref="JsonArraySourceOptions.Path"/> for what that guarantee does and does not cover once a
/// path is involved - but there is no equivalent here of <see cref="JsonLinesSource{TRow}.MalformedRows"/>:
/// a malformed element leaves the underlying reader's position inside the array unrecoverable, so
/// <see cref="ReadAsync"/> fails the whole read rather than skipping just that element. See
/// <see cref="JsonLinesSource{TRow}"/> for the counterpart that can skip a bad record, because a line
/// - unlike an array element - is a recovery boundary a parser can resynchronise on.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
public sealed class JsonArraySource<TRow> : IDataSource<TRow>, IAsyncInitializable
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly JsonArraySourceOptions _options;
    private readonly bool _ownsStream;

    private Stream? _stream;

    // The root case (Path empty) is handled by the framework's own streaming array reader; the
    // nested case (Path non-empty) needs the hand-rolled PipeReader-based one below, because
    // JsonSerializer.DeserializeAsyncEnumerable only ever reads an array at the document's root.
    private IAsyncEnumerator<TRow?>? _rootRows;
    private PipeReader? _nestedRows;
    private JsonReaderState _nestedState;

    private bool _exhausted;

    /// <summary>Reads <paramref name="file"/>.</summary>
    /// <param name="file">The file to read. Its filesystem is the one the source reads through.</param>
    /// <param name="options">Format settings. Defaults are camelCase, invariant, the document's root.</param>
    public JsonArraySource(IFileInfo file, JsonArraySourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        _options = options ?? new JsonArraySourceOptions();
        _ownsStream = true;
        _open = _ => new ValueTask<Stream>(file.OpenRead());
    }

    /// <summary>Reads a JSON array from a stream opened when the run starts.</summary>
    /// <param name="open">Opens the stream. Called once per run.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the stream.</param>
    public JsonArraySource(
        Func<CancellationToken, ValueTask<Stream>> open,
        JsonArraySourceOptions? options = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
        _options = options ?? new JsonArraySourceOptions();
        _ownsStream = !leaveOpen;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        _stream = await _open(cancellationToken).ConfigureAwait(false);

        if (_options.Path.Count == 0)
        {
            _rootRows = JsonSerializer
                .DeserializeAsyncEnumerable<TRow>(_stream, _options.SerializerOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            return;
        }

        // leaveOpen: true because this source, not the pipe, owns the underlying stream - disposal
        // is decided by _ownsStream in DisposeAsync.
        _nestedRows = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));
        _nestedState = await OpenNestedArrayAsync(_nestedRows, _options.Path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_rootRows is null && _nestedRows is null)
        {
            return Error.Failure(
                "json.not_initialized",
                $"{nameof(JsonArraySource<TRow>)} has no open stream. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first read.");
        }

        if (_exhausted)
        {
            return 0;
        }

        return _rootRows is not null
            ? await ReadRootArrayAsync(buffer, cancellationToken).ConfigureAwait(false)
            : await ReadNestedArrayAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ErrorOr<int>> ReadRootArrayAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool moved;
            try
            {
                moved = await _rootRows!.MoveNextAsync().ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // Unlike JsonLinesSource, there is no line to resynchronise on: the underlying
                // reader's position inside the array is now unrecoverable, so the read fails
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

            buffer.Span[count++] = _rootRows!.Current!;
        }

        return count;
    }

    private async ValueTask<ErrorOr<int>> ReadNestedArrayAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _nestedRows!.ReadAsync(cancellationToken).ConfigureAwait(false);
            var sequence = result.Buffer;

            // isFinalBlock is always false here, deliberately, even once result.IsCompleted is true:
            // it tells the reader "nothing follows in the whole document", which is only true for the
            // array's own closing "]" - every element before it is legitimately followed by more
            // bytes (a comma and the next element, or that "]"), and Utf8JsonReader (and
            // JsonSerializer.Deserialize(ref reader, ...) beneath it) rejects that trailing content
            // as invalid once told there cannot be any. "Not enough data at genuine end of stream" is
            // instead detected below, from the pipe's own IsCompleted.
            var peek = new Utf8JsonReader(sequence, isFinalBlock: false, _nestedState);
            if (!peek.Read())
            {
                _nestedRows.AdvanceTo(sequence.Start, sequence.End);
                if (result.IsCompleted)
                {
                    return Error.Validation("json.malformed_array", "The JSON array ended unexpectedly.");
                }

                continue;
            }

            if (peek.TokenType == JsonTokenType.EndArray)
            {
                _nestedState = peek.CurrentState;
                _nestedRows.AdvanceTo(sequence.GetPosition(peek.BytesConsumed));
                _exhausted = true;
                break;
            }

            // peek is now positioned right after reading the element's first token. Confirm the
            // whole element is buffered before touching the reader JsonSerializer will actually
            // consume from: TrySkip, unlike JsonSerializer.Deserialize, reports "not enough data yet"
            // by returning false rather than by throwing, and a struct copy lets it probe ahead
            // without disturbing peek's own position.
            var probe = peek;
            if (!probe.TrySkip())
            {
                _nestedRows.AdvanceTo(sequence.Start, sequence.End);
                if (result.IsCompleted)
                {
                    return Error.Validation("json.malformed_array", "The JSON array contains an incomplete element.");
                }

                continue;
            }

            var elementReader = peek;
            TRow? row;
            try
            {
                row = JsonSerializer.Deserialize<TRow>(ref elementReader, _options.SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Error.Validation(
                    "json.malformed_array",
                    $"The JSON array could not be parsed: {ex.Message}");
            }

            buffer.Span[count++] = row!;
            _nestedState = elementReader.CurrentState;
            _nestedRows.AdvanceTo(sequence.GetPosition(elementReader.BytesConsumed));
        }

        return count;
    }

    /// <summary>
    /// Walks <paramref name="path"/> from the root object down to its target array, returning the
    /// <see cref="JsonReaderState"/> positioned right after that array's opening <c>[</c>.
    /// </summary>
    private static async ValueTask<JsonReaderState> OpenNestedArrayAsync(
        PipeReader pipeReader,
        IReadOnlyList<string> path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryFindArrayStart(buffer, path, out var arrayStart, out var state))
            {
                pipeReader.AdvanceTo(arrayStart, buffer.End);
                return state;
            }

            if (result.IsCompleted)
            {
                throw new JsonException(
                    $"The JSON path '{string.Join('.', path)}' was not found, or does not lead to an array.");
            }

            // Not found in what is buffered so far: examine everything, consume nothing, and ask the
            // pipe for more. The whole walk is retried from the start of the buffer next time rather
            // than resumed - see the remarks on JsonArraySourceOptions.Path for why that trade-off is
            // fine here.
            pipeReader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static bool TryFindArrayStart(
        ReadOnlySequence<byte> buffer,
        IReadOnlyList<string> path,
        out SequencePosition arrayStart,
        out JsonReaderState state)
    {
        var reader = new Utf8JsonReader(buffer, isFinalBlock: false, state: default);
        arrayStart = default;
        state = default;

        if (!reader.Read())
        {
            return false;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object at the root when reading a nested array.");
        }

        for (var i = 0; i < path.Count; i++)
        {
            var segment = path[i];
            var isLast = i == path.Count - 1;

            // Search this object's properties for the one named by this path segment, skipping over
            // - without materialising - every value along the way that is not what we are looking for.
            while (true)
            {
                if (!reader.Read())
                {
                    return false;
                }

                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    throw new JsonException($"Property '{segment}' was not found.");
                }

                if (reader.ValueTextEquals(segment))
                {
                    break;
                }

                if (!reader.Read() || !reader.TrySkip())
                {
                    return false;
                }
            }

            // Matched: move onto this property's value.
            if (!reader.Read())
            {
                return false;
            }

            if (isLast)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    throw new JsonException($"Property '{segment}' is not a JSON array.");
                }

                arrayStart = buffer.GetPosition(reader.BytesConsumed);
                state = reader.CurrentState;
                return true;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Property '{segment}' is not a JSON object.");
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_rootRows is not null)
        {
            await _rootRows.DisposeAsync().ConfigureAwait(false);
            _rootRows = null;
        }

        if (_nestedRows is not null)
        {
            await _nestedRows.CompleteAsync().ConfigureAwait(false);
            _nestedRows = null;
        }

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
