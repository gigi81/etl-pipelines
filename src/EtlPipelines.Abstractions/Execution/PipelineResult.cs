namespace EtlPipelines.Abstractions.Execution;

/// <summary>
/// What a run did. Returned from <see cref="IPipeline.RunAsync"/> so a caller has something concrete
/// to log, report on, and derive a process exit code from — a pipeline that returns nothing cannot be
/// operated.
/// </summary>
/// <param name="Name">The pipeline's name.</param>
/// <param name="RowsRead">
/// Rows the first stage that moved any consumed. Coarse job stages that move no rows — a download,
/// a table swap — are skipped, so beginning a pipeline with one does not make this read zero.
/// </param>
/// <param name="RowsWritten">
/// Rows the last stage that moved any produced. Not necessarily equal to <paramref name="RowsRead"/>
/// — filters and aggregations change cardinality legitimately.
/// </param>
/// <param name="RowsFailed">Rows rejected across all stages.</param>
/// <param name="Elapsed">Total wall-clock time.</param>
/// <param name="Stages">Per-stage detail, in execution order.</param>
public sealed record PipelineResult(
    string Name,
    long RowsRead,
    long RowsWritten,
    long RowsFailed,
    TimeSpan Elapsed,
    IReadOnlyList<StageResult> Stages)
{
    /// <summary>Builds a pipeline result by aggregating stage results in execution order.</summary>
    public static PipelineResult FromStages(string name, IReadOnlyList<StageResult> stages, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(stages);

        long failed = 0;
        foreach (var stage in stages)
        {
            failed += stage.RowsFailed;
        }

        // Taken from the first and last stages that actually moved rows, rather than simply the first
        // and last. A coarse job stage - downloading the file, swapping a staging table into place -
        // moves no rows through the framework and reports none, and a pipeline that begins or ends
        // with one would otherwise report that it had read or written nothing at all.
        long read = 0;
        long written = 0;

        foreach (var stage in stages)
        {
            if (MovedRows(stage))
            {
                read = stage.RowsIn;
                break;
            }
        }

        for (var i = stages.Count - 1; i >= 0; i--)
        {
            if (MovedRows(stages[i]))
            {
                written = stages[i].RowsOut;
                break;
            }
        }

        return new PipelineResult(name, read, written, failed, elapsed, stages);
    }

    private static bool MovedRows(StageResult stage) =>
        stage.RowsIn > 0 || stage.RowsOut > 0 || stage.RowsFailed > 0;
}
