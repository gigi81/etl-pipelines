namespace EtlPipelines.Abstractions.Execution;

/// <summary>
/// What a run did. Returned from <see cref="IPipeline.RunAsync"/> so a caller has something concrete
/// to log, report on, and derive a process exit code from — a pipeline that returns nothing cannot be
/// operated.
/// </summary>
/// <param name="Name">The pipeline's name.</param>
/// <param name="RowsRead">Rows the first stage consumed.</param>
/// <param name="RowsWritten">
/// Rows the last stage produced. Not necessarily equal to <paramref name="RowsRead"/> — filters and
/// aggregations change cardinality legitimately.
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

        return new PipelineResult(
            name,
            stages.Count > 0 ? stages[0].RowsIn : 0,
            stages.Count > 0 ? stages[^1].RowsOut : 0,
            failed,
            elapsed,
            stages);
    }
}
