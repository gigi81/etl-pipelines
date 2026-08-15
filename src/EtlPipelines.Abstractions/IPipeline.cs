namespace EtlPipelines.Abstractions;

/// <summary>An ordered sequence of stages that can be run.</summary>
public interface IPipeline
{
    /// <summary>The pipeline's name, used for logging, tracing and metrics.</summary>
    string Name { get; }

    /// <summary>
    /// Runs every stage in order and reports what happened.
    /// </summary>
    /// <returns>
    /// A <see cref="PipelineResult"/> describing rows read, written and rejected plus per-stage
    /// detail, or the error that stopped the run.
    /// </returns>
    Task<ErrorOr<PipelineResult>> RunAsync(CancellationToken cancellationToken = default);
}
