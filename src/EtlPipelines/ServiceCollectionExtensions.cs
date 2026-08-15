using EtlPipelines.Abstractions;
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
    /// Registers a named pipeline. Resolve it later through <see cref="IPipelineFactory"/>.
    /// </summary>
    /// <remarks>
    /// The pipeline is composed lazily, on first resolution, so ports registered after this call are
    /// still visible to it — registration order in the container stays irrelevant, which is the whole
    /// reason stage order is tracked by the builder instead.
    /// </remarks>
    public static IServiceCollection AddEtlPipeline(
        this IServiceCollection services,
        string name,
        Action<IPipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IPipelineFactory, PipelineFactory>();
        services.AddSingleton(new PipelineRegistration(name, configure));

        return services;
    }
}

/// <summary>A pipeline definition awaiting a service provider to be built against.</summary>
internal sealed record PipelineRegistration(string Name, Action<IPipelineBuilder> Configure);

internal sealed class PipelineFactory : IPipelineFactory
{
    private readonly Dictionary<string, Lazy<IPipeline>> _pipelines;

    public PipelineFactory(IEnumerable<PipelineRegistration> registrations, IServiceProvider services)
    {
        _pipelines = new Dictionary<string, Lazy<IPipeline>>(StringComparer.Ordinal);

        foreach (var registration in registrations)
        {
            _pipelines[registration.Name] = new Lazy<IPipeline>(() =>
            {
                var builder = new EtlPipelineBuilder(registration.Name, services);
                registration.Configure(builder);
                return builder.Build();
            });
        }
    }

    public IReadOnlyCollection<string> Names => _pipelines.Keys;

    public IPipeline Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _pipelines.TryGetValue(name, out var pipeline)
            ? pipeline.Value
            : throw new InvalidOperationException(
                $"No pipeline named '{name}' is registered. Known pipelines: " +
                (_pipelines.Count == 0 ? "(none)" : string.Join(", ", _pipelines.Keys)));
    }
}
