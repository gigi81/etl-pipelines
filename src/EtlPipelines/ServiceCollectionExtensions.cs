using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EtlPipelines;

/// <summary>Resolves named pipelines that were registered into the container.</summary>
public interface IPipelineFactory
{
    /// <summary>Gets a registered pipeline by name.</summary>
    /// <exception cref="InvalidOperationException">No pipeline is registered under that name.</exception>
    IPipeline Get(string name);

    /// <summary>The names of every registered pipeline.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>Registers pipelines into a dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named pipeline and every component it names. Resolve it later through
    /// <see cref="IPipelineFactory"/> or <see cref="ServiceProviderExtensions.GetRequiredEtlPipeline"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder action runs immediately, because composing the pipeline is what registers its
    /// components. Each one goes in as <b>scoped</b> and <b>keyed to this pipeline's name</b>: scoped
    /// so every run gets its own instances from the scope the run creates, keyed so two pipelines can
    /// use the same component type without one overwriting the other's registration.
    /// </para>
    /// <para>
    /// The pipeline object itself is assembled on first resolution, once a provider exists.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddEtlPipeline(
        this IServiceCollection services,
        string name,
        Action<IPipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new EtlPipelineBuilder(name, services);
        configure(builder);

        services.TryAddSingleton<IPipelineFactory, PipelineFactory>();
        services.AddSingleton(builder.CreateBlueprint());

        return services;
    }
}

internal sealed class PipelineFactory : IPipelineFactory
{
    private readonly Dictionary<string, IPipeline> _pipelines;

    public PipelineFactory(IEnumerable<PipelineBlueprint> blueprints, IServiceProvider services)
    {
        _pipelines = new Dictionary<string, IPipeline>(StringComparer.Ordinal);

        foreach (var blueprint in blueprints)
        {
            // The pipeline is stateless — every run builds its own scope, its own nodes and its own
            // components — so one instance per name is safe to share, including across concurrent runs.
            _pipelines[blueprint.Name] = new EtlPipeline(blueprint, services);
        }
    }

    public IReadOnlyCollection<string> Names => _pipelines.Keys;

    public IPipeline Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _pipelines.TryGetValue(name, out var pipeline)
            ? pipeline
            : throw new InvalidOperationException(
                $"No pipeline named '{name}' is registered. Known pipelines: " +
                (_pipelines.Count == 0 ? "(none)" : string.Join(", ", _pipelines.Keys)));
    }
}
