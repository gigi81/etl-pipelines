
namespace EtlPipelines.Transforms;

/// <summary>
/// Groups rows by key across batch boundaries, holding every group in memory until input ends.
/// </summary>
/// <remarks>
/// <para>
/// This is the general aggregation: it makes no assumption about input ordering, so it cannot know a
/// group is finished until the last row has arrived. That makes it a <b>fully blocking</b> stage —
/// downstream sees nothing until upstream is exhausted, and back-pressure does not bound its memory,
/// which is O(distinct keys). That is inherent to hash aggregation, not a defect here.
/// </para>
/// <para>
/// When the source can return rows ordered by the grouping key — in ETL usually just an
/// <c>ORDER BY</c> away — prefer <see cref="SortedAggregateTransform{TIn,TKey,TState,TOut}"/>, which
/// holds one group instead of all of them and keeps rows flowing.
/// </para>
/// <para>
/// For very large aggregations, doing the work in the source database beats pulling rows out to
/// aggregate them in process. This framework is a pipeline, not a query engine.
/// </para>
/// </remarks>
public abstract class HashAggregateTransform<TIn, TKey, TState, TOut>
    : IDataTransform<TIn, TOut>, IDrainable<TOut>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TState> _groups;
    private IEnumerator<KeyValuePair<TKey, TState>>? _drain;

    /// <summary>Creates the aggregate, optionally with a custom key comparer.</summary>
    protected HashAggregateTransform(IEqualityComparer<TKey>? keyComparer = null) =>
        _groups = new Dictionary<TKey, TState>(keyComparer);

    /// <summary>Groups seen so far. Useful for tests and for reporting cardinality.</summary>
    public int GroupCount => _groups.Count;

    /// <summary>Extracts the grouping key from a row.</summary>
    protected abstract TKey GetKey(in TIn row);

    /// <summary>Creates the initial accumulator for a newly seen key.</summary>
    protected abstract TState Seed(TKey key);

    /// <summary>Folds a row into the accumulator for its group.</summary>
    protected abstract TState Accumulate(TState state, in TIn row);

    /// <summary>Projects a finished group into an output row.</summary>
    protected abstract TOut GetResult(TKey key, TState state);

    /// <inheritdoc />
    public ValueTask<ErrorOr<TransformResult>> TransformAsync(
        ReadOnlyMemory<TIn> input,
        Memory<TOut> output,
        CancellationToken cancellationToken)
    {
        var source = input.Span;

        for (var i = 0; i < source.Length; i++)
        {
            ref readonly var row = ref source[i];
            var key = GetKey(in row);

            // CollectionsMarshal-free path: a miss seeds, a hit folds. Both write back once.
            _groups[key] = _groups.TryGetValue(key, out var state)
                ? Accumulate(state, in row)
                : Accumulate(Seed(key), in row);
        }

        // Consumes everything, produces nothing: results appear during the drain.
        return ValueTask.FromResult<ErrorOr<TransformResult>>(new TransformResult(source.Length, 0));
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<int>> DrainAsync(Memory<TOut> output, CancellationToken cancellationToken)
    {
        _drain ??= _groups.GetEnumerator();

        var destination = output.Span;
        var produced = 0;

        while (produced < destination.Length && _drain.MoveNext())
        {
            var (key, state) = _drain.Current;
            destination[produced++] = GetResult(key, state);
        }

        return ValueTask.FromResult<ErrorOr<int>>(produced);
    }
}

/// <summary>
/// Groups rows by key assuming the input already arrives ordered by that key, emitting each group as
/// soon as the key changes.
/// </summary>
/// <remarks>
/// This is the aggregation you want whenever the data allows it. Because a key change proves the
/// previous group is complete, it holds exactly one accumulator rather than one per distinct key, and
/// it is only <i>semi</i>-blocking: rows keep flowing downstream throughout the run instead of
/// stalling until input is exhausted. The cost is a precondition the framework cannot check cheaply —
/// if the input is not actually sorted by key, a key will be grouped once per contiguous run of it,
/// silently splitting groups.
/// </remarks>
public abstract class SortedAggregateTransform<TIn, TKey, TState, TOut>
    : IDataTransform<TIn, TOut>, IDrainable<TOut>
    where TKey : notnull
{
    private readonly IEqualityComparer<TKey> _keyComparer;
    private TKey _currentKey = default!;
    private TState _currentState = default!;
    private bool _hasCurrent;
    private bool _drained;

    /// <summary>Creates the aggregate, optionally with a custom key comparer.</summary>
    protected SortedAggregateTransform(IEqualityComparer<TKey>? keyComparer = null) =>
        _keyComparer = keyComparer ?? EqualityComparer<TKey>.Default;

    /// <summary>Extracts the grouping key from a row.</summary>
    protected abstract TKey GetKey(in TIn row);

    /// <summary>Creates the initial accumulator for a newly seen key.</summary>
    protected abstract TState Seed(TKey key);

    /// <summary>Folds a row into the accumulator for its group.</summary>
    protected abstract TState Accumulate(TState state, in TIn row);

    /// <summary>Projects a finished group into an output row.</summary>
    protected abstract TOut GetResult(TKey key, TState state);

    /// <inheritdoc />
    public ValueTask<ErrorOr<TransformResult>> TransformAsync(
        ReadOnlyMemory<TIn> input,
        Memory<TOut> output,
        CancellationToken cancellationToken)
    {
        var source = input.Span;
        var destination = output.Span;

        var produced = 0;
        var consumed = 0;

        while (consumed < source.Length)
        {
            ref readonly var row = ref source[consumed];
            var key = GetKey(in row);

            if (_hasCurrent && !_keyComparer.Equals(key, _currentKey))
            {
                // The group just ended. If there is nowhere to put it, stop without consuming this
                // row — the runtime re-invokes with a fresh buffer and the same remaining input.
                if (produced == destination.Length)
                {
                    break;
                }

                destination[produced++] = GetResult(_currentKey, _currentState);
                _hasCurrent = false;
            }

            if (!_hasCurrent)
            {
                _currentKey = key;
                _currentState = Seed(key);
                _hasCurrent = true;
            }

            _currentState = Accumulate(_currentState, in row);
            consumed++;
        }

        return ValueTask.FromResult<ErrorOr<TransformResult>>(new TransformResult(consumed, produced));
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<int>> DrainAsync(Memory<TOut> output, CancellationToken cancellationToken)
    {
        // Only the final in-flight group remains; every earlier one was emitted at its key change.
        if (_drained || !_hasCurrent || output.IsEmpty)
        {
            return ValueTask.FromResult<ErrorOr<int>>(0);
        }

        output.Span[0] = GetResult(_currentKey, _currentState);
        _drained = true;
        return ValueTask.FromResult<ErrorOr<int>>(1);
    }
}
