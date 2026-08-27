using System.Diagnostics;

namespace EtlPipelines.Core.Runtime;

/// <summary>
/// Runs several independent, already-compiled stage chains concurrently, as one entry in the outer
/// pipeline's stage list.
/// </summary>
/// <remarks>
/// <para>
/// Each branch is itself an ordered list of stages - typically a coarse stage followed by a dataflow,
/// the same shape <see cref="EtlPipelineBuilder.Parallel"/> composed it from - and runs that list
/// through <see cref="StageExecutor"/> exactly as the top-level run loop would, one stage after
/// another. It is the branches that overlap with each other; nothing inside one branch runs out of
/// order with the rest of that same branch.
/// </para>
/// <para>
/// Row counts are rolled up the way <see cref="PipelineResult.FromStages"/> already rolls up a whole
/// pipeline's stages into one summary: each branch's own "rows read" and "rows written" - skipping the
/// coarse stages that moved none - are summed across branches into this block's single
/// <see cref="StageResult"/>. <see cref="StageResult.Elapsed"/> is this block's own wall-clock time,
/// not the sum of the branches', since they ran at the same time.
/// </para>
/// </remarks>
internal sealed class ParallelStage(IReadOnlyList<IReadOnlyList<Func<IServiceProvider, IPipelineStage>>> branches)
    : IPipelineStage
{
    public string Name { get; } = $"Parallel({branches.Count})";

    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        var outcomes = await Task.WhenAll(
            branches.Select((stages, index) => RunBranchAsync(index, stages, context, cancellationToken)))
            .ConfigureAwait(false);

        long rowsIn = 0;
        long rowsOut = 0;
        long rowsFailed = 0;
        Error? firstError = null;

        foreach (var (branch, error) in outcomes)
        {
            rowsIn += branch.RowsRead;
            rowsOut += branch.RowsWritten;
            rowsFailed += branch.RowsFailed;
            firstError ??= error;
        }

        var result = new StageResult(Name, rowsIn, rowsOut, rowsFailed, started.Elapsed);

        return firstError is { } failure ? failure.WithStageResult(result) : result;
    }

    /// <summary>
    /// Runs one branch's stages in order, stopping at its own first failure - a branch is not
    /// transactional across its stages any more than the top-level run is - and folds what it managed
    /// into a single <see cref="PipelineResult"/> so the caller need not know how many stages it took.
    /// </summary>
    private static async Task<(PipelineResult Branch, Error? Error)> RunBranchAsync(
        int index,
        IReadOnlyList<Func<IServiceProvider, IPipelineStage>> stages,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var results = new List<StageResult>(stages.Count);

        foreach (var resolve in stages)
        {
            var stage = resolve(context.Services);
            var result = await StageExecutor.RunAsync(stage, context, cancellationToken).ConfigureAwait(false);

            if (result.IsError)
            {
                // What this stage moved before it failed still rides along on the error - see
                // EtlDiagnostics.WithStageResult - so a branch that dies on its second of three
                // stages still contributes what its first stage did.
                results.Add(EtlDiagnostics.StageResultOf(result.FirstError) ?? new StageResult(stage.Name, 0, 0, 0, TimeSpan.Zero));
                return (PipelineResult.FromStages($"branch {index}", results, started.Elapsed), result.FirstError);
            }

            results.Add(result.Value);
        }

        return (PipelineResult.FromStages($"branch {index}", results, started.Elapsed), null);
    }
}
