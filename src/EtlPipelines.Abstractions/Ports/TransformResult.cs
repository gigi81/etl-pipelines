using EtlPipelines.Abstractions.Configuration;

namespace EtlPipelines.Abstractions.Ports;

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
