using EtlPipelines.Abstractions;

namespace EtlPipelines.Transforms;

/// <summary>
/// Base class for stateless row-at-a-time transforms. Implement <see cref="Transform"/> and the
/// batching, buffer accounting and partial-consumption bookkeeping are supplied for you.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Transform"/> is deliberately synchronous. Most transforms are pure CPU — parse, map,
/// validate, reshape — and routing those through an async state machine once per row costs more than
/// the work itself. Transforms that genuinely need to await something (a lookup, an enrichment call)
/// belong on <c>SelectAsync</c> instead.
/// </para>
/// <para>
/// This class maps or rejects; it does not filter. Dropping rows is <c>Where</c>'s job, which keeps
/// <see cref="ErrorOr{TValue}"/> honest here — an error means the row was bad, not merely unwanted.
/// </para>
/// </remarks>
public abstract class RowTransform<TIn, TOut> : IDataTransform<TIn, TOut>
{
    /// <summary>
    /// Transforms one row. Return an error to reject it; the pipeline then applies the configured
    /// <see cref="RowErrorAction"/>.
    /// </summary>
    protected abstract ErrorOr<TOut> Transform(in TIn row);

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

        while (consumed < source.Length && produced < destination.Length)
        {
            var result = Transform(in source[consumed]);
            consumed++;

            if (result.IsError)
            {
                // Stop at the rejected row and hand back the progress made before it. The runtime
                // applies the row-error policy and re-invokes us with the remaining input.
                return Completed(TransformResult.Rejected(consumed, produced, result.FirstError));
            }

            destination[produced++] = result.Value;
        }

        return Completed(new TransformResult(consumed, produced));
    }

    private static ValueTask<ErrorOr<TransformResult>> Completed(TransformResult result) =>
        ValueTask.FromResult<ErrorOr<TransformResult>>(result);
}

/// <summary>
/// Base class for stateless filters. Rows failing <see cref="Include"/> are dropped without being
/// counted as failures.
/// </summary>
public abstract class RowFilter<TRow> : IDataTransform<TRow, TRow>
{
    /// <summary>Returns whether the row should continue down the pipeline.</summary>
    protected abstract bool Include(in TRow row);

    /// <inheritdoc />
    public ValueTask<ErrorOr<TransformResult>> TransformAsync(
        ReadOnlyMemory<TRow> input,
        Memory<TRow> output,
        CancellationToken cancellationToken)
    {
        var source = input.Span;
        var destination = output.Span;

        var produced = 0;
        var consumed = 0;

        while (consumed < source.Length && produced < destination.Length)
        {
            ref readonly var row = ref source[consumed];
            consumed++;

            if (Include(in row))
            {
                destination[produced++] = row;
            }
        }

        return ValueTask.FromResult<ErrorOr<TransformResult>>(new TransformResult(consumed, produced));
    }
}
