using Albatross.CommandLine.Annotations;
using EtlPipelines.Hosting.Handlers;

namespace EtlPipelines.Hosting.Commands;

/// <summary>Runs one registered pipeline, or all of them.</summary>
[Verb<RunPipelineHandler>("run", Description = "Runs a registered pipeline by name, or every one of them when no name is given.")]
public class RunPipelineParams
{
    /// <summary>
    /// The pipeline to run, as it was named in <c>AddEtlPipeline</c>. Left out, every registered
    /// pipeline runs in the order it was registered.
    /// </summary>
    /// <remarks>
    /// Nullable, which is how this library's command line generator is told an argument is optional.
    /// </remarks>
    [Argument(Description = "The name the pipeline was registered under. Omit to run all of them.")]
    public string? Pipeline { get; init; }
}
