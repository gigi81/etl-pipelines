using Albatross.CommandLine.Annotations;
using EtlPipelines.Hosting.Handlers;

namespace EtlPipelines.Hosting.Commands;

/// <summary>Runs one registered pipeline.</summary>
[Verb<RunPipelineHandler>("run", Description = "Runs a registered pipeline by name.")]
public class RunPipelineParams
{
    /// <summary>The pipeline to run, as it was named in <c>AddEtlPipeline</c>.</summary>
    [Argument(Description = "The name the pipeline was registered under.")]
    public required string Pipeline { get; init; }
}
