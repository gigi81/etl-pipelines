using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Abstractions.Ports;

namespace EtlPipelines.Abstractions.Lifecycle;

/// <summary>
/// Opt-in capability: the runtime calls <see cref="InitializeAsync"/> once, before any rows flow.
/// </summary>
/// <remarks>
/// This is deliberately not a base interface of <see cref="IPipelineStage"/>, <see cref="IDataSource{TRow}"/>
/// or <see cref="IDataSink{TRow}"/>. Baking it into those contracts would force every implementation
/// to carry an empty <c>InitializeAsync</c> and would stop a stage from being expressed as a lambda.
/// Instead the runtime probes for it with a type test, so components that need setup opt in and
/// components that do not never see it.
/// </remarks>
public interface IAsyncInitializable
{
    /// <summary>
    /// Prepares the component. Opening connections, creating destination tables, and validating
    /// configuration belong here rather than in a constructor, which cannot be asynchronous.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken);
}
