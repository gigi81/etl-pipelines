using System.CommandLine;
using Albatross.CommandLine;
using EtlPipelines.Hosting.Commands;

namespace EtlPipelines.Hosting.Handlers;

/// <summary>Handles <c>run</c>.</summary>
public class RunPipelineHandler : BaseHandler<RunPipelineParams>
{
    private readonly PipelineRunner _runner;

    /// <summary>Takes the runner every command line front end shares.</summary>
    public RunPipelineHandler(ParseResult result, RunPipelineParams parameters, PipelineRunner runner)
        : base(result, parameters)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    public override Task<int> InvokeAsync(CancellationToken cancellationToken) =>
        _runner.RunAsync(parameters.Pipeline, cancellationToken);
}
