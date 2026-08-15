namespace EtlPipelines.Abstractions;

/// <summary>
/// The outcome of a single <see cref="IDataTransform{TIn,TOut}.TransformAsync"/> call: how many input
/// rows were consumed and how many output rows were produced. The two need not be equal.
/// </summary>
/// <param name="Consumed">
/// Rows taken from the head of the input. May be fewer than the input length when the output buffer
/// filled first; the runtime then re-invokes the transform with the remaining input.
/// </param>
/// <param name="Produced">Rows written to the head of the output buffer.</param>
public readonly record struct TransformResult(int Consumed, int Produced)
{
    /// <summary>
    /// Set when the last consumed row was rejected, in which case <see cref="Consumed"/> includes it
    /// and <see cref="Produced"/> covers only the rows before it.
    /// </summary>
    /// <remarks>
    /// Row rejection is reported here in the success value rather than as an
    /// <see cref="ErrorOr{TValue}"/> error, because the two failures are not the same kind of thing.
    /// A rejected row is fatal only to that row: the pipeline applies its
    /// <see cref="RowErrorAction"/>, records it, and carries on with the rest of the batch — so the
    /// partial progress made before it must survive, which an error result cannot carry. Returning an
    /// error from <see cref="IDataTransform{TIn,TOut}.TransformAsync"/> means the failure is fatal to
    /// the whole stage.
    /// </remarks>
    public Error? RejectedRow { get; init; }

    /// <summary>Reports progress interrupted by a rejected row at the end of the consumed span.</summary>
    public static TransformResult Rejected(int consumed, int produced, Error error) =>
        new(consumed, produced) { RejectedRow = error };
}

/// <summary>
/// Transforms batches of rows. The transform side of an ETL pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Reporting <see cref="TransformResult.Consumed"/> separately from
/// <see cref="TransformResult.Produced"/> is what lets a single contract express every cardinality:
/// </para>
/// <list type="table">
///   <item><term>1:1 map</term><description>consumed = n, produced = n</description></item>
///   <item><term>filter</term><description>consumed = n, produced &lt;= n</description></item>
///   <item><term>reduce within a batch</term><description>consumed = n, produced = 1</description></item>
///   <item><term>1:many expansion</term><description>consumed &lt; n when the output buffer fills</description></item>
/// </list>
/// <para>
/// The runtime loops while <c>Consumed &lt; input.Length</c>, re-slicing the input, so an
/// implementation is free to stop early whenever the output buffer runs out of room. The only
/// guarantee it gets in return is <c>output.Length &gt;= 1</c>, which is enough to make progress
/// impossible to deadlock. Returning <c>Consumed == 0</c> with a non-empty input and
/// <c>Produced == 0</c> is a contract violation — it would spin forever — and the runtime throws.
/// </para>
/// <para>
/// Transforms that accumulate state across batches (aggregations, grouping) must also implement
/// <see cref="IDrainable{TOut}"/> to emit their results once input is exhausted; without it their
/// state is silently discarded.
/// </para>
/// <para>
/// Most implementations should derive from one of the base classes in the <c>EtlPipelines</c> package
/// (<c>RowTransform</c>, <c>HashAggregateTransform</c>, <c>SortedAggregateTransform</c>) rather than
/// implementing this interface directly; those handle the consumed/produced bookkeeping.
/// </para>
/// </remarks>
/// <typeparam name="TIn">Input row type.</typeparam>
/// <typeparam name="TOut">Output row type.</typeparam>
public interface IDataTransform<TIn, TOut>
{
    /// <summary>
    /// Consumes rows from <paramref name="input"/> and produces rows into <paramref name="output"/>.
    /// </summary>
    /// <param name="input">
    /// Rows to process. Must not be retained past the call — the buffer is pooled and recycled.
    /// </param>
    /// <param name="output">Destination buffer, guaranteed to have room for at least one row.</param>
    /// <param name="cancellationToken">Cancels the transform.</param>
    ValueTask<ErrorOr<TransformResult>> TransformAsync(
        ReadOnlyMemory<TIn> input,
        Memory<TOut> output,
        CancellationToken cancellationToken);
}
