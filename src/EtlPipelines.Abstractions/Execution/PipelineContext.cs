using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Abstractions.Ports;

namespace EtlPipelines.Abstractions.Execution;

/// <summary>
/// Ambient state for one pipeline run, handed to each stage as it executes.
/// </summary>
/// <remarks>
/// The <see cref="Items"/> bag exists for coarse job stages that genuinely need to hand something to
/// a later stage — a downloaded file path, a batch id issued at the start of the run. It is
/// explicitly <i>not</i> the mechanism by which rows flow: that is what the typed
/// <see cref="IDataSource{TRow}"/>/<see cref="IDataTransform{TIn,TOut}"/>/<see cref="IDataSink{TRow}"/>
/// ports are for, and routing row data through a string-keyed dictionary would throw away the
/// compile-time checking those ports exist to provide.
/// </remarks>
public sealed class PipelineContext
{
    /// <summary>Creates a context for a run.</summary>
    public PipelineContext(string pipelineName, IServiceProvider services, PipelineOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        PipelineName = pipelineName;
        Services = services;
        Options = options;
    }

    /// <summary>The name of the pipeline being run.</summary>
    public string PipelineName { get; }

    /// <summary>Scoped service provider for this run. Stages resolve their dependencies from here.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The options this run was configured with.</summary>
    public PipelineOptions Options { get; }

    /// <summary>Identifies this run in logs and traces.</summary>
    public Guid RunId { get; } = Guid.NewGuid();

    /// <summary>When the run started.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Free-form state shared between coarse job stages. Not a row-transport mechanism.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
