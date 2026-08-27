using System.Diagnostics;

namespace EtlPipelines.Core.Runtime;

/// <summary>
/// Runs one stage and records it — the span, the metrics, and (on failure) the row counts it managed
/// to move — exactly once, regardless of what is driving the stage list.
/// </summary>
/// <remarks>
/// Pulled out of <see cref="EtlPipeline.RunAsync"/> so <see cref="ParallelStage"/> can run its own
/// branches through the identical path: a stage inside a parallel branch gets the same span, the same
/// metric, and the same on-failure row accounting as one running directly in the top-level list. There
/// is exactly one place that decides what "recording a stage" means.
/// </remarks>
internal static class StageExecutor
{
    /// <summary>
    /// Executes <paramref name="stage"/>, tracing and recording it. On failure, the returned error
    /// still carries whatever the stage moved — recoverable with <see cref="EtlDiagnostics.StageResultOf"/> —
    /// the same contract <see cref="Runtime.DataflowStage"/> already honours for its own callers.
    /// </summary>
    internal static async ValueTask<ErrorOr<StageResult>> RunAsync(
        IPipelineStage stage,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var stageStarted = Stopwatch.GetTimestamp();
        using var stageActivity = EtlDiagnostics.StartStage(context, stage.Name);

        var result = await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);

        if (result.IsError)
        {
            // A stage that knows what it moved says so through the error - see
            // EtlDiagnostics.WithStageResult - and one that does not is recorded with zero counts and
            // this call's own elapsed time.
            var failed = EtlDiagnostics.StageResultOf(result.FirstError)
                ?? new StageResult(stage.Name, 0, 0, 0, Stopwatch.GetElapsedTime(stageStarted));

            EtlDiagnostics.RecordStage(context, stageActivity, failed, result.FirstError);
            return result.FirstError.WithStageResult(failed);
        }

        EtlDiagnostics.RecordStage(context, stageActivity, result.Value);
        return result.Value;
    }
}
