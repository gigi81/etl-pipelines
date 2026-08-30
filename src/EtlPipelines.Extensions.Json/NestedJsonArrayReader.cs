using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Reads elements from a JSON array nested under <see cref="JsonArraySourceOptions.Path"/>, one row
/// at a time, without buffering the array itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>JsonSerializer.DeserializeAsyncEnumerable</c> - what <see cref="RootJsonArrayReader{TRow}"/>
/// delegates to - only ever reads an array at the document's root, so a nested one needs this type's
/// own hand-rolled walk: a <see cref="PipeReader"/> drives an <see cref="Utf8JsonReader"/> down
/// <see cref="JsonArraySourceOptions.Path"/> to find the array's opening <c>[</c>
/// (<see cref="OpenAsync"/>), then <see cref="ReadAsync"/> takes over one element at a time from
/// there.
/// </para>
/// <para>
/// A subtlety this relies on: neither <c>JsonSerializer.Deserialize(ref Utf8JsonReader, ...)</c> nor
/// <c>JsonDocument.TryParseValue</c> will parse one element correctly if handed a buffer with more
/// content after it - both reject that as invalid trailing data, even mid-array, even with
/// <c>isFinalBlock: false</c>. So every element read first proves the whole element is already
/// buffered with a throwaway <see cref="Utf8JsonReader"/> copy's <see cref="Utf8JsonReader.TrySkip()"/>
/// - which reports "not enough data yet" by returning <see langword="false"/>, never by throwing -
/// and only once that succeeds does a second, real copy get handed to the serializer.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
internal sealed class NestedJsonArrayReader<TRow> : IJsonArrayReader<TRow>
{
    private readonly PipeReader _pipeReader;
    private readonly JsonSerializerOptions _serializerOptions;

    private JsonReaderState _state;
    private bool _exhausted;

    private NestedJsonArrayReader(PipeReader pipeReader, JsonSerializerOptions serializerOptions, JsonReaderState state)
    {
        _pipeReader = pipeReader;
        _serializerOptions = serializerOptions;
        _state = state;
    }

    /// <summary>
    /// Opens <paramref name="stream"/> and walks <paramref name="path"/> down to its target array,
    /// leaving the reader positioned right after that array's opening <c>[</c>.
    /// </summary>
    /// <exception cref="JsonException">
    /// A property in <paramref name="path"/> is missing, or the path does not lead through JSON
    /// objects to a JSON array.
    /// </exception>
    public static async ValueTask<NestedJsonArrayReader<TRow>> OpenAsync(
        Stream stream,
        IReadOnlyList<string> path,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        // leaveOpen: true because the source that owns this reader, not the pipe, owns the
        // underlying stream.
        var pipeReader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        var state = await NavigateAsync(pipeReader, path, cancellationToken).ConfigureAwait(false);
        return new NestedJsonArrayReader<TRow>(pipeReader, serializerOptions, state);
    }

    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_exhausted)
        {
            return 0;
        }

        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _pipeReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var sequence = result.Buffer;

            // isFinalBlock is always false here, deliberately, even once result.IsCompleted is true:
            // it tells the reader "nothing follows in the whole document", which is only true for the
            // array's own closing "]" - every element before it is legitimately followed by more
            // bytes (a comma and the next element, or that "]"), and the trailing-content check
            // described in the type's remarks would reject that. "Not enough data at genuine end of
            // stream" is instead detected below, from the pipe's own IsCompleted.
            var peek = new Utf8JsonReader(sequence, isFinalBlock: false, _state);
            if (!peek.Read())
            {
                _pipeReader.AdvanceTo(sequence.Start, sequence.End);
                if (result.IsCompleted)
                {
                    return Error.Validation("json.malformed_array", "The JSON array ended unexpectedly.");
                }

                continue;
            }

            if (peek.TokenType == JsonTokenType.EndArray)
            {
                _state = peek.CurrentState;
                _pipeReader.AdvanceTo(sequence.GetPosition(peek.BytesConsumed));
                _exhausted = true;
                break;
            }

            // peek is now positioned right after reading the element's first token. Confirm the
            // whole element is buffered - see the type's remarks - before touching the reader the
            // serializer will actually consume from.
            var probe = peek;
            if (!probe.TrySkip())
            {
                _pipeReader.AdvanceTo(sequence.Start, sequence.End);
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
                row = JsonSerializer.Deserialize<TRow>(ref elementReader, _serializerOptions);
            }
            catch (JsonException ex)
            {
                return Error.Validation(
                    "json.malformed_array",
                    $"The JSON array could not be parsed: {ex.Message}");
            }

            buffer.Span[count++] = row!;
            _state = elementReader.CurrentState;
            _pipeReader.AdvanceTo(sequence.GetPosition(elementReader.BytesConsumed));
        }

        return count;
    }

    public ValueTask DisposeAsync() => _pipeReader.CompleteAsync();

    /// <summary>
    /// Walks <paramref name="path"/> from the root object down to its target array, returning the
    /// <see cref="JsonReaderState"/> positioned right after that array's opening <c>[</c>.
    /// </summary>
    private static async ValueTask<JsonReaderState> NavigateAsync(
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
}
