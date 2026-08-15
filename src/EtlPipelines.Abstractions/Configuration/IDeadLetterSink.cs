namespace EtlPipelines.Abstractions.Configuration;

/// <summary>
/// Receives rows rejected by the pipeline when <see cref="PipelineOptions.OnRowError"/> is
/// <see cref="RowErrorAction.DeadLetter"/>.
/// </summary>
/// <remarks>
/// Bad-row tolerance is a first-class ETL concern — the same need SSIS covers with error outputs and
/// <c>bcp</c> with <c>-m maxerrors</c>. Rejecting a row must not cost the whole run, but the rejected
/// rows still have to go somewhere inspectable.
/// </remarks>
/// <typeparam name="TRow">The row type being rejected.</typeparam>
public interface IDeadLetterSink<TRow>
{
    /// <summary>Records a single rejected row together with the error that rejected it.</summary>
    ValueTask WriteAsync(TRow row, Error error, CancellationToken cancellationToken);
}
