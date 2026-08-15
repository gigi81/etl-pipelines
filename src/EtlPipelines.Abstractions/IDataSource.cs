namespace EtlPipelines.Abstractions;

/// <summary>
/// Produces rows into caller-supplied buffers. The extract side of an ETL pipeline.
/// </summary>
/// <remarks>
/// The contract mirrors <see cref="System.IO.Stream.ReadAsync(Memory{byte}, CancellationToken)"/>:
/// fill as much of the buffer as is available, return how many rows were written, and return zero
/// once the source is exhausted. There is deliberately no <c>HasMoreData</c> property — a count of
/// zero is the single, unambiguous end-of-data signal, valid before the first read and after an
/// error alike.
/// </remarks>
/// <typeparam name="TRow">The row type produced by this source.</typeparam>
public interface IDataSource<TRow> : IAsyncDisposable
{
    /// <summary>
    /// Fills up to <paramref name="buffer"/>.Length rows starting at index 0.
    /// </summary>
    /// <param name="buffer">
    /// Destination buffer. The runtime rents these from <see cref="System.Buffers.ArrayPool{T}"/> and
    /// recycles them, so the source must not retain the <see cref="Memory{T}"/> past this call.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The number of rows written to <paramref name="buffer"/>, or <c>0</c> when no rows remain.
    /// A short read (fewer rows than the buffer holds) does <b>not</b> imply end of data; only zero does.
    /// </returns>
    ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken);
}
