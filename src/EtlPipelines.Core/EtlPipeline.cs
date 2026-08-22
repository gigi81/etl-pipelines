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
            var result = await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);

            if (result.IsError)
            {
                EtlDiagnostics.RecordRun(context, activity, started.Elapsed, result.FirstError);
                return result.Errors;
            }

            results.Add(result.Value);
        }

        var run = PipelineResult.FromStages(Name, results, started.Elapsed);

        EtlDiagnostics.RecordRun(context, activity, run.Elapsed);

        return run;
    }
}
