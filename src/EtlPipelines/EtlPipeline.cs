using System.Diagnostics;
using EtlPipelines.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines;

/// <summary>Runs an ordered list of stages and reports what they did.</summary>
public sealed class EtlPipeline : IPipeline
{
    private readonly IReadOnlyList<Func<IServiceProvider, IPipelineStage>> _stages;
    private readonly IServiceProvider _services;
    private readonly PipelineOptions _options;

    internal EtlPipeline(
        string name,
        IReadOnlyList<Func<IServiceProvider, IPipelineStage>> stages,
        IServiceProvider services,
        PipelineOptions options)
    {
        Name = name;
        _stages = stages;
        _services = services;
        _options = options;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Starts a standalone pipeline with its own service container, for consoles and tests that are
    /// not already hosting one. Applications with a container should prefer
    /// <see cref="ServiceCollectionExtensions.AddEtlPipeline"/> and inject <see cref="IPipelineFactory"/>.
    /// </summary>
    public static IPipelineBuilder CreateBuilder(string name = "default") =>
        new EtlPipelineBuilder(name, new ServiceCollection());

    /// <inheritdoc />
    public async Task<ErrorOr<PipelineResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        using var activity = EtlDiagnostics.ActivitySource.StartActivity($"etl.pipeline {Name}");

        // Each run gets its own scope so stateful ports — an aggregate's accumulator, a sink's open
        // transaction — start clean and are disposed together when the run ends.
        await using var scope = _services.CreateAsyncScope();
        var context = new PipelineContext(Name, scope.ServiceProvider, _options);

        activity?.SetTag("etl.run_id", context.RunId);

        var results = new List<StageResult>(_stages.Count);

        foreach (var factory in _stages)
        {
            var stage = factory(scope.ServiceProvider);
            var result = await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);

            if (result.IsError)
            {
                activity?.SetStatus(ActivityStatusCode.Error, result.FirstError.Description);
                return result.Errors;
            }

            results.Add(result.Value);
        }

        return PipelineResult.FromStages(Name, results, Stopwatch.GetElapsedTime(started));
    }
}
