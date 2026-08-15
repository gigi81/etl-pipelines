using EtlPipelines.Runtime;
using EtlPipelines.Transforms;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines;

/// <summary>
/// Accumulates the steps of a typed dataflow, registering each port into the container as it goes.
/// </summary>
/// <remarks>
/// Every chaining method returns a builder re-typed to the row type now flowing, so a step whose
/// input does not match the previous step's output simply will not compile. Ports are registered
/// scoped and keyed to the pipeline, exactly as coarse stages are, so a run's scope owns and disposes
/// every one of them.
/// </remarks>
internal sealed class DataflowBuilder<TRow>(EtlPipelineBuilder owner, List<DataflowNode> nodes)
    : IDataflowBuilder<TRow>
{
    /// <summary>
    /// Tracks whether the last appended step keeps state across batches. Set by the builder methods
    /// that create such steps, so <see cref="WithParallelism"/> can reject them immediately rather
    /// than waiting for the run.
    /// </summary>
    private bool _lastStepIsStateful;

    public DataflowBuilder(EtlPipelineBuilder owner, IEnumerable<DataflowNode> nodes)
        : this(owner, nodes.ToList())
    {
    }

    public IDataflowBuilder<TOut> Through<TTransform, TOut>()
        where TTransform : class, IDataTransform<TRow, TOut>
    {
        var key = owner.NextKey("transform");
        owner.Services.AddKeyedScoped<IDataTransform<TRow, TOut>, TTransform>(key);

        return Append(
            typeof(TTransform).Name,
            EtlPipelineBuilder.Keyed<IDataTransform<TRow, TOut>>(key),
            // Known statically here, unlike the container-resolved overload, so a stateful transform
            // is caught by WithParallelism at build time instead of at the run.
            stateful: typeof(TTransform).IsAssignableTo(typeof(IDrainable<TOut>)));
    }

    public IDataflowBuilder<TOut> Through<TOut>() =>
        Append(
            $"{typeof(TRow).Name}->{typeof(TOut).Name}",
            EtlPipelineBuilder.Required<IDataTransform<TRow, TOut>>,
            stateful: false);

    public IDataflowBuilder<TOut> Through<TOut>(IDataTransform<TRow, TOut> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Register<TOut>(transform.GetType().Name, (_, _) => transform, transform is IDrainable<TOut>);
    }

    public IDataflowBuilder<TOut> Through<TOut>(Func<IServiceProvider, IDataTransform<TRow, TOut>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // The concrete type is unknown until the factory runs, so a drainable transform here is
        // caught at run time by TransformNode rather than now.
        return Register<TOut>($"{typeof(TRow).Name}->{typeof(TOut).Name}", (services, _) => factory(services), stateful: false);
    }

    public IDataflowBuilder<TOut> Select<TOut>(Func<TRow, TOut> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        Func<TRow, ErrorOr<TOut>> lifted = row => map(row);
        return TrySelect(lifted);
    }

    public IDataflowBuilder<TOut> TrySelect<TOut>(Func<TRow, ErrorOr<TOut>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Register<TOut>("Select", (_, _) => new SelectTransform<TRow, TOut>(map), stateful: false);
    }

    public IDataflowBuilder<TOut> SelectAsync<TOut>(Func<TRow, CancellationToken, ValueTask<ErrorOr<TOut>>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Register<TOut>("SelectAsync", (_, _) => new SelectAsyncTransform<TRow, TOut>(map), stateful: false);
    }

    public IDataflowBuilder<TRow> Where(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Register<TRow>("Where", (_, _) => new WhereTransform<TRow>(predicate), stateful: false);
    }

    public IDataflowBuilder<TOut> SelectMany<TOut>(Func<TRow, IEnumerable<TOut>> expand)
    {
        ArgumentNullException.ThrowIfNull(expand);

        // Expansion holds a half-drained enumerator between calls, so it is per-worker state even
        // though it is not an aggregate.
        return Register<TOut>("SelectMany", (_, _) => new ExpandTransform<TRow, TOut>(expand), stateful: true);
    }

    public IDataflowBuilder<TOut> GroupBy<TKey, TState, TOut>(
        Func<TRow, TKey> keySelector,
        Func<TKey, TState> seed,
        Func<TState, TRow, TState> accumulate,
        Func<TKey, TState, TOut> resultSelector,
        bool inputIsSortedByKey = false)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(accumulate);
        ArgumentNullException.ThrowIfNull(resultSelector);

        return Register<TOut>(
            inputIsSortedByKey ? "GroupBy(sorted)" : "GroupBy",
            (_, _) => inputIsSortedByKey
                ? new DelegateSortedAggregate<TRow, TKey, TState, TOut>(keySelector, seed, accumulate, resultSelector)
                : new DelegateHashAggregate<TRow, TKey, TState, TOut>(keySelector, seed, accumulate, resultSelector),
            stateful: true);
    }

    public IDataflowBuilder<TRow> WithParallelism(int degreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);

        if (nodes.Count == 0 || nodes[^1] is not IParallelizable parallelizable)
        {
            throw new InvalidOperationException(
                "WithParallelism applies to the transform just added; call it directly after one.");
        }

        if (_lastStepIsStateful && degreeOfParallelism > 1)
        {
            throw new InvalidOperationException(
                $"'{nodes[^1].Name}' carries state across batches, so it cannot run at a parallelism of " +
                $"{degreeOfParallelism}: each worker would accumulate and emit its own partial result, " +
                "silently producing wrong output. Remove WithParallelism, or pre-partition by key.");
        }

        parallelizable.DegreeOfParallelism = degreeOfParallelism;
        return this;
    }

    public IPipelineBuilder To<TSink>() where TSink : class, IDataSink<TRow>
    {
        var key = owner.NextKey("sink");
        owner.Services.AddKeyedScoped<IDataSink<TRow>, TSink>(key);
        return Terminate(typeof(TSink).Name, EtlPipelineBuilder.Keyed<IDataSink<TRow>>(key));
    }

    public IPipelineBuilder To() =>
        Terminate(typeof(TRow).Name, EtlPipelineBuilder.Required<IDataSink<TRow>>);

    public IPipelineBuilder To(object serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        return Terminate(typeof(TRow).Name, EtlPipelineBuilder.Keyed<IDataSink<TRow>>(serviceKey));
    }

    public IPipelineBuilder To(IDataSink<TRow> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var key = owner.NextKey("sink");
        owner.Services.AddKeyedScoped<IDataSink<TRow>>(key, (_, _) => sink);
        return Terminate(sink.GetType().Name, EtlPipelineBuilder.Keyed<IDataSink<TRow>>(key));
    }

    public IPipelineBuilder To(Func<IServiceProvider, IDataSink<TRow>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var key = owner.NextKey("sink");
        owner.Services.AddKeyedScoped<IDataSink<TRow>>(key, (services, _) => factory(services));
        return Terminate(typeof(TRow).Name, EtlPipelineBuilder.Keyed<IDataSink<TRow>>(key));
    }

    /// <summary>Registers a transform built from a delegate, then appends it.</summary>
    private DataflowBuilder<TOut> Register<TOut>(
        string name,
        Func<IServiceProvider, object?, IDataTransform<TRow, TOut>> implementation,
        bool stateful)
    {
        var key = owner.NextKey("transform");
        owner.Services.AddKeyedScoped(key, implementation);
        return Append(name, EtlPipelineBuilder.Keyed<IDataTransform<TRow, TOut>>(key), stateful);
    }

    private DataflowBuilder<TOut> Append<TOut>(
        string name,
        Func<IServiceProvider, IDataTransform<TRow, TOut>> factory,
        bool stateful)
    {
        nodes.Add(new TransformNode<TRow, TOut>(name, factory));
        return new DataflowBuilder<TOut>(owner, nodes) { _lastStepIsStateful = stateful };
    }

    private IPipelineBuilder Terminate(string name, Func<IServiceProvider, IDataSink<TRow>> factory)
    {
        nodes.Add(new SinkNode<TRow>(name, factory));
        owner.AddDataflow(nodes);
        return owner;
    }
}
