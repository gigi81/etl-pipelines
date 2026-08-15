namespace EtlPipelines.Abstractions;

/// <summary>
/// Consumes batches of rows. The load side of an ETL pipeline.
/// </summary>
/// <remarks>
/// Sinks that buffer internally — bulk-copy writers, transactional writers — should implement
/// <see cref="IAsyncCompletable"/> so they receive an explicit commit signal at end of stream.
/// Committing from <see cref="IAsyncDisposable.DisposeAsync"/> is not equivalent: disposal also runs
/// on the failure path, where committing is exactly the wrong thing to do.
/// </remarks>
/// <typeparam name="TRow">The row type consumed by this sink.</typeparam>
public interface IDataSink<TRow> : IAsyncDisposable
{
    /// <summary>
    /// Writes a batch of rows.
    /// </summary>
    /// <param name="batch">
    /// The rows to write. <b>The sink must not retain this memory past the call.</b> The runtime rents
    /// batch buffers from <see cref="System.Buffers.ArrayPool{T}"/> and reuses them immediately, so a
    /// retained <see cref="ReadOnlyMemory{T}"/> will observe torn data from a later batch. Copy out
    /// anything that needs to outlive the call.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The number of rows actually persisted.</returns>
    ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken);
}
