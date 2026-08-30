using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Reads elements from a JSON array at the document's root, via the framework's own
/// <c>JsonSerializer.DeserializeAsyncEnumerable</c>.
/// </summary>
/// <remarks>
/// This is the fast path: <c>DeserializeAsyncEnumerable</c> only ever reads an array at the root, so
/// this type does no parsing of its own at all, and exists mainly to present that enumerator through
/// the same <see cref="IJsonArrayReader{TRow}"/> seam <see cref="NestedJsonArrayReader{TRow}"/> does.
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
internal sealed class RootJsonArrayReader<TRow> : IJsonArrayReader<TRow>
{
    private readonly IAsyncEnumerator<TRow?> _rows;
    private bool _exhausted;

    public RootJsonArrayReader(Stream stream, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        _rows = JsonSerializer
            .DeserializeAsyncEnumerable<TRow>(stream, serializerOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
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

            bool moved;
            try
            {
                moved = await _rows.MoveNextAsync().ConfigureAwait(false);
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

            buffer.Span[count++] = _rows.Current!;
        }

        return count;
    }

    public ValueTask DisposeAsync() => _rows.DisposeAsync();
}
