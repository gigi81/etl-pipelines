namespace EtlPipelines.Abstractions;

/// <summary>
/// Composes a pipeline from stages and typed dataflows.
/// </summary>
/// <remarks>
/// The builder keeps its own ordered list of stage descriptors rather than relying on service
/// registration order. Registering a stage type into a container records no position, which is why
/// composition and dependency registration are kept as separate concerns here.
/// </remarks>
public interface IPipelineBuilder
{
    /// <summary>Adjusts batch size, channel capacity and row-error policy for this pipeline.</summary>
    IPipelineBuilder WithOptions(Action<PipelineOptions> configure);

    /// <summary>Appends a coarse job stage, resolved from the service provider at run time.</summary>
    IPipelineBuilder AddStage<TStage>(string? name = null) where TStage : class, IPipelineStage;

    /// <summary>Appends an already-constructed stage.</summary>
    IPipelineBuilder AddStage(IPipelineStage stage);

    /// <summary>Appends a stage defined inline. Useful for glue steps not worth a class.</summary>
    IPipelineBuilder AddStage(
        string name,
        Func<PipelineContext, CancellationToken, ValueTask<ErrorOr<Success>>> execute);

    /// <summary>
    /// Begins a typed dataflow whose source is resolved from the service provider.
    /// </summary>
    /// <remarks>
    /// Only the row type is named. Spelling the source's concrete type as well would mean writing
    /// <c>From&lt;CsvSource, Order&gt;()</c> at every call site, because C# has no partial generic
    /// inference — naming just the row type keeps the meaningful half and lets the terminating
    /// <see cref="IDataflowBuilder{TRow}.To()"/> infer completely.
    /// </remarks>
    IDataflowBuilder<TRow> From<TRow>();

    /// <summary>Begins a typed dataflow from a keyed service, for when two sources share a row type.</summary>
    IDataflowBuilder<TRow> From<TRow>(object serviceKey);

    /// <summary>Begins a typed dataflow from an existing source instance.</summary>
    IDataflowBuilder<TRow> From<TRow>(IDataSource<TRow> source);

    /// <summary>Begins a typed dataflow from a source factory.</summary>
    IDataflowBuilder<TRow> From<TRow>(Func<IServiceProvider, IDataSource<TRow>> factory);

    /// <summary>Produces the runnable pipeline.</summary>
    IPipeline Build();
}

/// <summary>
/// Composes the transform chain of a typed dataflow. Each method returns a builder re-typed to the
/// new row type, so a mismatch between one step's output and the next step's input is a compile
/// error rather than a run-time surprise.
/// </summary>
/// <typeparam name="TRow">The row type currently flowing.</typeparam>
public interface IDataflowBuilder<TRow>
{
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

    /// <summary>Terminates the dataflow with a sink resolved from the service provider.</summary>
    IPipelineBuilder To();

    /// <summary>Terminates the dataflow with a keyed sink.</summary>
    IPipelineBuilder To(object serviceKey);

    /// <summary>Terminates the dataflow with an existing sink instance.</summary>
    IPipelineBuilder To(IDataSink<TRow> sink);

    /// <summary>Terminates the dataflow with a sink factory.</summary>
    IPipelineBuilder To(Func<IServiceProvider, IDataSink<TRow>> factory);
}
