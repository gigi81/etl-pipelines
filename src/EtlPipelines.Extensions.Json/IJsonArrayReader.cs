namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Reads elements one at a time from an already-open JSON array.
/// </summary>
/// <remarks>
/// The seam <see cref="JsonArraySource{TRow}"/> delegates to once it knows, from
/// <see cref="JsonArraySourceOptions.Path"/>, whether the array it needs is at the document's root or
/// nested inside it - two questions with genuinely different answers, implemented by
/// <see cref="RootJsonArrayReader{TRow}"/> and <see cref="NestedJsonArrayReader{TRow}"/> respectively
/// rather than as branches of one method. <see cref="JsonArraySource{TRow}"/> itself only ever talks
/// to this interface: opening the stream, picking which implementation to construct, and closing it
/// again is all it does.
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
internal interface IJsonArrayReader<TRow> : IAsyncDisposable
{
    /// <summary>
    /// Fills up to <paramref name="buffer"/>.Length rows starting at index 0, the same contract as
    /// <see cref="IDataSource{TRow}.ReadAsync"/> - a return of <c>0</c> means the array is exhausted.
    /// </summary>
    ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken);
}
