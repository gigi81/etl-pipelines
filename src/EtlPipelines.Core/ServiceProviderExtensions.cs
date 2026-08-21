using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Core;

/// <summary>Resolves pipelines straight from a service provider.</summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Gets a pipeline registered with
    /// <see cref="ServiceCollectionExtensions.AddEtlPipeline"/> by name.
    /// </summary>
    /// <param name="services">The provider to resolve from.</param>
    /// <param name="name">The name the pipeline was registered under.</param>
    /// <exception cref="InvalidOperationException">
    /// No pipelines are registered at all, or none under that name.
    /// </exception>
    public static IPipeline GetRequiredEtlPipeline(this IServiceProvider services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Resolved by hand rather than through GetRequiredService so that the two ways this can fail
        // stay distinguishable: nothing registered at all is a different mistake from a wrong name,
        // and the generic "no service for type IPipelineFactory" message names neither.
        if (services.GetService(typeof(IPipelineFactory)) is not IPipelineFactory factory)
        {
            throw new InvalidOperationException(
                $"No ETL pipelines are registered, so '{name}' cannot be resolved. " +
                "Call services.AddEtlPipeline(name, builder => ...) during startup.");
        }

        return factory.Get(name);
    }
}
