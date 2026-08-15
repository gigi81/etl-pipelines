using EtlPipelines.Runtime;
using EtlPipelines.Transforms;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines;

/// <summary>
/// Accumulates the steps of a typed dataflow.
/// </summary>
/// <remarks>
/// Every chaining method returns a builder re-typed to the row type now flowing, so a step whose
/// input does not match the previous step's output simply will not compile. The node list is shared
/// and appended to as the chain grows; the terminating <c>To</c> hands it to the pipeline builder.
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

    public IDataflowBuilder<TOut> Through<TOut>() =>
        Through(EtlPipelineBuilder.Required<IDataTransform<TRow, TOut>>);

    public IDataflowBuilder<TOut> Through<TOut>(IDataTransform<TRow, TOut> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Append<TOut>(transform.GetType().Name, _ => transform, transform is IDrainable<TOut>);
    }

    public IDataflowBuilder<TOut> Through<TOut>(Func<IServiceProvider, IDataTransform<TRow, TOut>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // The concrete type is unknown until the container resolves it, so a drainable transform
        // here is caught at run time by TransformNode rather than now.
        return Append<TOut>($"{typeof(TRow).Name}->{typeof(TOut).Name}", factory, stateful: false);
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
        return Append<TOut>("Select", _ => new SelectTransform<TRow, TOut>(map), stateful: false);
    }

    public IDataflowBuilder<TOut> SelectAsync<TOut>(Func<TRow, CancellationToken, ValueTask<ErrorOr<TOut>>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return Append<TOut>("SelectAsync", _ => new SelectAsyncTransform<TRow, TOut>(map), stateful: false);
    }

    public IDataflowBuilder<TRow> Where(Func<TRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Append<TRow>("Where", _ => new WhereTransform<TRow>(predicate), stateful: false);
    }

    public IDataflowBuilder<TOut> SelectMany<TOut>(Func<TRow, IEnumerable<TOut>> expand)
    {
        ArgumentNullException.ThrowIfNull(expand);

        // Expansion holds a half-drained enumerator between calls, so it is per-worker state even
        // though it is not an aggregate.
        return Append<TOut>("SelectMany", _ => new ExpandTransform<TRow, TOut>(expand), stateful: true);
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

        return Append<TOut>(
            inputIsSortedByKey ? "GroupBy(sorted)" : "GroupBy",
            _ => inputIsSortedByKey
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

    public IPipelineBuilder To() => To(EtlPipelineBuilder.Required<IDataSink<TRow>>);

    public IPipelineBuilder To(object serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);

        return To(services => services is IKeyedServiceProvider keyed
            && keyed.GetKeyedService(typeof(IDataSink<TRow>), serviceKey) is IDataSink<TRow> sink
            ? sink
            : throw new InvalidOperationException(
                $"No IDataSink<{typeof(TRow).Name}> is registered with key '{serviceKey}'."));
    }

    public IPipelineBuilder To(IDataSink<TRow> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return To(_ => sink);
    }

    public IPipelineBuilder To(Func<IServiceProvider, IDataSink<TRow>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        nodes.Add(new SinkNode<TRow>(typeof(TRow).Name, factory));
        owner.AddDataflow(nodes);
        return owner;
    }

    private DataflowBuilder<TOut> Append<TOut>(
        string name,
        Func<IServiceProvider, IDataTransform<TRow, TOut>> factory,
        bool stateful)
    {
        nodes.Add(new TransformNode<TRow, TOut>(name, factory));
        return new DataflowBuilder<TOut>(owner, nodes) { _lastStepIsStateful = stateful };
    }
}
