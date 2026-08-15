
namespace EtlPipelines.Transforms;

/// <summary>Wraps a mapping delegate as a 1:1 transform.</summary>
internal sealed class SelectTransform<TIn, TOut>(Func<TIn, ErrorOr<TOut>> map) : RowTransform<TIn, TOut>
{
    protected override ErrorOr<TOut> Transform(in TIn row) => map(row);
}

/// <summary>Wraps a predicate as a filter.</summary>
internal sealed class WhereTransform<TRow>(Func<TRow, bool> predicate) : RowFilter<TRow>
{
    protected override bool Include(in TRow row) => predicate(row);
}

/// <summary>
/// Wraps an asynchronous mapping delegate. Unlike <see cref="SelectTransform{TIn,TOut}"/> this pays
/// for an async state machine per row, so it is only worth it when the delegate genuinely awaits —
/// a lookup or an enrichment call.
/// </summary>
internal sealed class SelectAsyncTransform<TIn, TOut>(
    Func<TIn, CancellationToken, ValueTask<ErrorOr<TOut>>> map) : IDataTransform<TIn, TOut>
{
    public async ValueTask<ErrorOr<TransformResult>> TransformAsync(
        ReadOnlyMemory<TIn> input,
        Memory<TOut> output,
        CancellationToken cancellationToken)
    {
        var produced = 0;
        var consumed = 0;

        while (consumed < input.Length && produced < output.Length)
        {
            var result = await map(input.Span[consumed], cancellationToken).ConfigureAwait(false);
            consumed++;

            if (result.IsError)
            {
                return TransformResult.Rejected(consumed, produced, result.FirstError);
            }

            output.Span[produced++] = result.Value;
        }

        return new TransformResult(consumed, produced);
    }
}

/// <summary>
/// Wraps an expansion delegate as a 1:many transform.
/// </summary>
/// <remarks>
/// Expansion is the case that forces <see cref="TransformResult.Consumed"/> to exist separately from
/// <see cref="TransformResult.Produced"/>: one input row can produce more output rows than the buffer
/// holds. When that happens this keeps the half-drained enumerator and reports the input row as
/// consumed, so the next invocation resumes mid-row rather than losing or repeating values.
/// </remarks>
/// <remarks>
/// It is also <see cref="IDrainable{TOut}"/>, which is less obvious than it looks. When the very last
/// input row overflows the buffer, its remainder has nowhere to go: the input is exhausted, so no
/// further <c>TransformAsync</c> call is coming. That leftover is state outliving the input — the
/// same condition an aggregation is in — and without the drain it is silently dropped.
/// </remarks>
internal sealed class ExpandTransform<TIn, TOut>(Func<TIn, IEnumerable<TOut>> expand)
    : IDataTransform<TIn, TOut>, IDrainable<TOut>, IAsyncDisposable
{
    private IEnumerator<TOut>? _pending;

    public ValueTask<ErrorOr<TransformResult>> TransformAsync(
        ReadOnlyMemory<TIn> input,
        Memory<TOut> output,
        CancellationToken cancellationToken)
    {
        var destination = output.Span;
        var produced = 0;

        // Finish the expansion left half-drained by the previous call before touching new input.
        if (_pending is not null)
        {
            produced = Drain(_pending, destination, produced);

            if (produced == destination.Length)
            {
                // Still possibly mid-row: consume nothing so the remaining input is offered again.
                return Completed(new TransformResult(0, produced));
            }

            _pending.Dispose();
            _pending = null;
        }

        var source = input.Span;
        var consumed = 0;

        while (consumed < source.Length && produced < destination.Length)
        {
            var enumerator = expand(source[consumed]).GetEnumerator();
            consumed++;

            produced = Drain(enumerator, destination, produced);

            if (produced == destination.Length)
            {
                // The buffer filled; this row may have more to give. Hold the enumerator rather than
                // re-expanding the row next time, which would duplicate the values already emitted.
                _pending = enumerator;
                break;
            }

            enumerator.Dispose();
        }

        return Completed(new TransformResult(consumed, produced));
    }

    /// <summary>
    /// Emits whatever the final input row had left over after the buffer filled. Reached only when
    /// input ended mid-expansion, which is the one case <c>TransformAsync</c> cannot recover from on
    /// its own.
    /// </summary>
    public ValueTask<ErrorOr<int>> DrainAsync(Memory<TOut> output, CancellationToken cancellationToken)
    {
        if (_pending is null)
        {
            return ValueTask.FromResult<ErrorOr<int>>(0);
        }

        var produced = Drain(_pending, output.Span, 0);

        if (produced < output.Length)
        {
            _pending.Dispose();
            _pending = null;
        }

        return ValueTask.FromResult<ErrorOr<int>>(produced);
    }

    private static int Drain(IEnumerator<TOut> enumerator, Span<TOut> destination, int produced)
    {
        while (produced < destination.Length && enumerator.MoveNext())
        {
            destination[produced++] = enumerator.Current;
        }

        return produced;
    }

    public ValueTask DisposeAsync()
    {
        _pending?.Dispose();
        _pending = null;
        return ValueTask.CompletedTask;
    }

    private static ValueTask<ErrorOr<TransformResult>> Completed(TransformResult result) =>
        ValueTask.FromResult<ErrorOr<TransformResult>>(result);
}

/// <summary>Delegate-driven hash aggregate, backing <c>GroupBy</c> with unsorted input.</summary>
internal sealed class DelegateHashAggregate<TIn, TKey, TState, TOut>(
    Func<TIn, TKey> keySelector,
    Func<TKey, TState> seed,
    Func<TState, TIn, TState> accumulate,
    Func<TKey, TState, TOut> resultSelector)
    : HashAggregateTransform<TIn, TKey, TState, TOut>
    where TKey : notnull
{
    protected override TKey GetKey(in TIn row) => keySelector(row);
    protected override TState Seed(TKey key) => seed(key);
    protected override TState Accumulate(TState state, in TIn row) => accumulate(state, row);
    protected override TOut GetResult(TKey key, TState state) => resultSelector(key, state);
}

/// <summary>Delegate-driven sorted aggregate, backing <c>GroupBy</c> with key-ordered input.</summary>
internal sealed class DelegateSortedAggregate<TIn, TKey, TState, TOut>(
    Func<TIn, TKey> keySelector,
    Func<TKey, TState> seed,
    Func<TState, TIn, TState> accumulate,
    Func<TKey, TState, TOut> resultSelector)
    : SortedAggregateTransform<TIn, TKey, TState, TOut>
    where TKey : notnull
{
    protected override TKey GetKey(in TIn row) => keySelector(row);
    protected override TState Seed(TKey key) => seed(key);
    protected override TState Accumulate(TState state, in TIn row) => accumulate(state, row);
    protected override TOut GetResult(TKey key, TState state) => resultSelector(key, state);
}
