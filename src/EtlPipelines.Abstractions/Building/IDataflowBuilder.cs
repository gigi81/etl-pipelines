using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Abstractions.Ports;

namespace EtlPipelines.Abstractions.Building;

/// <summary>
/// Composes the transform chain of a typed dataflow. Each method returns a builder re-typed to the
/// new row type, so a mismatch between one step's output and the next step's input is a compile
/// error rather than a run-time surprise.
/// </summary>
/// <typeparam name="TRow">The row type currently flowing.</typeparam>
public interface IDataflowBuilder<TRow>
{
    /// <summary>
    /// Appends a transform the pipeline constructs and owns, built fresh for each run.
    /// </summary>
    /// <remarks>
    /// Naming the concrete type here is the registration; constructor dependencies still come from
    /// the container. Because the type is known statically, an <see cref="IDrainable{TOut}"/>
    /// implementation is detected at build time, so <see cref="WithParallelism"/> can reject a
    /// stateful transform immediately rather than at the run.
    /// </remarks>
    IDataflowBuilder<TOut> Through<TTransform, TOut>() where TTransform : class, IDataTransform<TRow, TOut>;

    /// <summary>Appends a transform resolved from the service provider.</summary>
    IDataflowBuilder<TOut> Through<TOut>();

    /// <summary>Appends an existing transform instance.</summary>
    IDataflowBuilder<TOut> Through<TOut>(IDataTransform<TRow, TOut> transform);

    /// <summary>Appends a transform from a factory.</summary>
    IDataflowBuilder<TOut> Through<TOut>(Func<IServiceProvider, IDataTransform<TRow, TOut>> factory);

    /// <summary>Appends a 1:1 row map.</summary>
    IDataflowBuilder<TOut> Select<TOut>(Func<TRow, TOut> map);

    /// <summary>
    /// Appends a 1:1 row map that can reject rows. Rejected rows are handled by the configured
    /// <see cref="RowErrorAction"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not an overload of <see cref="Select{TOut}(Func{TRow,TOut})"/>. Because
    /// <see cref="ErrorOr{TValue}"/> converts implicitly from its value type, two overloads differing
    /// only in the delegate's return type would be ambiguous at many call sites and would silently
    /// bind to the wrong one at others.
    /// </remarks>
    IDataflowBuilder<TOut> TrySelect<TOut>(Func<TRow, ErrorOr<TOut>> map);

    /// <summary>Appends an asynchronous 1:1 row map, for lookups and enrichment calls.</summary>
    IDataflowBuilder<TOut> SelectAsync<TOut>(Func<TRow, CancellationToken, ValueTask<ErrorOr<TOut>>> map);

    /// <summary>Appends a filter.</summary>
    IDataflowBuilder<TRow> Where(Func<TRow, bool> predicate);

    /// <summary>
    /// Appends a 1:many expansion. The runtime handles the case where the expansion overflows the
    /// output buffer by re-invoking with the unconsumed input.
    /// </summary>
    IDataflowBuilder<TOut> SelectMany<TOut>(Func<TRow, IEnumerable<TOut>> expand);

    /// <summary>
    /// Appends an aggregation that accumulates across batch boundaries and emits once input ends.
    /// </summary>
    /// <param name="keySelector">Extracts the grouping key.</param>
    /// <param name="seed">Creates the initial accumulator for a newly seen key.</param>
    /// <param name="accumulate">Folds a row into the accumulator.</param>
    /// <param name="resultSelector">Projects a finished group to an output row.</param>
    /// <param name="inputIsSortedByKey">
    /// Set this when the source already returns rows ordered by <paramref name="keySelector"/> — in
    /// ETL that is usually free, needing only an <c>ORDER BY</c> in the source query. It lets the
    /// aggregate emit each group as the key changes, so memory stays O(one group) and rows keep
    /// flowing downstream instead of stalling until end of input. Left <see langword="false"/>, the
    /// aggregate hashes every key and is fully blocking, with memory O(distinct keys).
    /// </param>
    IDataflowBuilder<TOut> GroupBy<TKey, TState, TOut>(
        Func<TRow, TKey> keySelector,
        Func<TKey, TState> seed,
        Func<TState, TRow, TState> accumulate,
        Func<TKey, TState, TOut> resultSelector,
        bool inputIsSortedByKey = false)
        where TKey : notnull;

    /// <summary>
    /// Runs the most recently appended transform at the given degree of parallelism.
    /// </summary>
    /// <remarks>
    /// Only valid for stateless transforms, and it does not preserve row order. Requesting it for a
    /// transform that implements <see cref="IDrainable{TOut}"/> throws: parallel instances would each
    /// accumulate a partial aggregate and drain it separately, producing silently wrong results.
    /// Throwing beats corrupting. The check is immediate for inline and instance transforms; for one
    /// resolved from the container the concrete type is unknown until the run, so it throws there.
    /// </remarks>
    IDataflowBuilder<TRow> WithParallelism(int degreeOfParallelism);

    /// <summary>
    /// Terminates the dataflow with a sink the pipeline constructs and owns, built fresh per run.
    /// </summary>
    /// <remarks>
    /// Only the sink type is named — the row type is already known here, so unlike
    /// <c>From</c> and <c>Through</c> this needs no second type argument.
    /// </remarks>
    IPipelineBuilder To<TSink>() where TSink : class, IDataSink<TRow>;

    /// <summary>Terminates the dataflow with a sink resolved from the service provider.</summary>
    IPipelineBuilder To();

    /// <summary>Terminates the dataflow with a keyed sink.</summary>
    IPipelineBuilder To(object serviceKey);

    /// <summary>Terminates the dataflow with an existing sink instance.</summary>
    IPipelineBuilder To(IDataSink<TRow> sink);

    /// <summary>Terminates the dataflow with a sink factory.</summary>
    IPipelineBuilder To(Func<IServiceProvider, IDataSink<TRow>> factory);
}
