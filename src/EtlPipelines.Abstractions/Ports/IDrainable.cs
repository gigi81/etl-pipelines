using EtlPipelines.Abstractions.Lifecycle;

namespace EtlPipelines.Abstractions.Ports;

/// <summary>
/// Implemented by transforms that hold state across batches and must emit rows once input ends.
/// </summary>
/// <remarks>
/// <para>
/// Cross-batch aggregation — a <c>GROUP BY</c> over millions of rows arriving in ten-thousand-row
/// chunks — accumulates through every batch and produces its results only after the last one. That
/// needs an end-of-input signal capable of <i>producing rows</i>, which <see cref="IAsyncCompletable"/>
/// is not: completion is a commit signal and returns no data. Without this interface an aggregating
/// transform would swallow its entire input and emit nothing.
/// </para>
/// <para>
/// The drain may exceed a single buffer — a million distinct groups do not fit in a ten-thousand-row
/// output — so it is called repeatedly until it returns zero. That is deliberately the same contract
/// as <see cref="IDataSource{TRow}.ReadAsync"/>: at end of input, an aggregating transform simply
/// becomes a source.
/// </para>
/// <para>
/// A drainable transform is stateful, and therefore must never be run at a parallelism degree above
/// one. The builder rejects that combination rather than silently producing wrong aggregates.
/// </para>
/// </remarks>
/// <typeparam name="TOut">The row type emitted during the drain.</typeparam>
public interface IDrainable<TOut>
{
    /// <summary>
    /// Emits buffered results after the input stream has completed. Called repeatedly until it
    /// returns <c>0</c>.
    /// </summary>
    /// <param name="output">Destination buffer, guaranteed to have room for at least one row.</param>
    /// <param name="cancellationToken">Cancels the drain.</param>
    /// <returns>Rows written to <paramref name="output"/>, or <c>0</c> when the drain is complete.</returns>
    ValueTask<ErrorOr<int>> DrainAsync(Memory<TOut> output, CancellationToken cancellationToken);
}
