using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Core;

/// <summary>Runs an ordered list of stages and reports what they did.</summary>
public sealed class EtlPipeline : IPipeline
{
    private readonly PipelineBlueprint _blueprint;
    private readonly IServiceProvider _services;

    internal EtlPipeline(PipelineBlueprint blueprint, IServiceProvider services)
    {
        _blueprint = blueprint;
        _services = services;
    }

    /// <inheritdoc />
    public string Name => _blueprint.Name;

    /// <summary>
    /// Starts a standalone pipeline with its own service container, for consoles and tests that are
    /// not already hosting one. Applications with a container should prefer
    /// <see cref="ServiceCollectionExtensions.AddEtlPipeline"/>.
    /// </summary>
    public static IPipelineBuilder CreateBuilder(string name = "default") =>
        new EtlPipelineBuilder(name, new ServiceCollection());

    /// <inheritdoc />
    public async Task<ErrorOr<PipelineResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();

        // One scope per run, and every component is registered scoped, so each run resolves its own
        // stages and ports and the scope disposes all of them together when the run ends. Two runs of
        // the same pipeline — sequential or concurrent — therefore share nothing.
        await using var scope = _services.CreateAsyncScope();
        var context = new PipelineContext(Name, scope.ServiceProvider, _blueprint.Options);

        // After the context, because the span is tagged with the run it belongs to. Building and
        // tearing down the scope then falls outside the span - which is the more honest boundary
        // anyway, since PipelineResult.Elapsed does not count them either.
        using var activity = EtlDiagnostics.StartRun(context);

        var results = new List<StageResult>(_blueprint.Stages.Count);

        foreach (var resolve in _blueprint.Stages)
        {
            var stage = resolve(scope.ServiceProvider);

            // Opened here rather than inside each stage, so every stage is traced - a coarse
            // IPipelineStage published nothing at all before this - and traced exactly once. A
            // stage that opened its own span underneath this one (DataflowStage used to) would be
            // reported twice.
            var stageStarted = Stopwatch.GetTimestamp();
            using var stageActivity = EtlDiagnostics.StartStage(context, stage.Name);

            var result = await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);

            if (result.IsError)
            {
                // Recorded on the failure path too. A stage that died after half a million rows
                // still consumed them, and a span left with no outcome is the one a reader most
                // needs to see. A stage that knows what it moved says so through the error - see
                // EtlDiagnostics.WithStageResult - and one that does not is recorded with zero
                // counts and this loop's own elapsed time.
                var failed = EtlDiagnostics.StageResultOf(result.FirstError)
                    ?? new StageResult(stage.Name, 0, 0, 0, Stopwatch.GetElapsedTime(stageStarted));

                EtlDiagnostics.RecordStage(context, stageActivity, failed, result.FirstError);
                EtlDiagnostics.RecordRun(context, activity, started.Elapsed, result.FirstError);
                return result.Errors;
            }

            EtlDiagnostics.RecordStage(context, stageActivity, result.Value);
            results.Add(result.Value);
        }

        var run = PipelineResult.FromStages(Name, results, started.Elapsed);

        EtlDiagnostics.RecordRun(context, activity, run.Elapsed);

        return run;
    }
}
