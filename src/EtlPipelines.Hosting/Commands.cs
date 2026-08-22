using System.CommandLine;
using Albatross.CommandLine;
using Albatross.CommandLine.Annotations;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Hosting;

/// <summary>Runs one registered pipeline.</summary>
[Verb<RunPipelineHandler>("run", Description = "Runs a registered pipeline by name.")]
public class RunPipelineParams
{
    /// <summary>The pipeline to run, as it was named in <c>AddEtlPipeline</c>.</summary>
    [Argument(Description = "The name the pipeline was registered under.")]
    public required string Pipeline { get; init; }
}

/// <summary>Handles <c>run</c>.</summary>
public class RunPipelineHandler : BaseHandler<RunPipelineParams>
{
    private readonly PipelineRunner _runner;

    /// <summary>Takes the runner every command line front end shares.</summary>
    public RunPipelineHandler(ParseResult result, RunPipelineParams parameters, PipelineRunner runner)
        : base(result, parameters)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
    }

    /// <inheritdoc />
    public override Task<int> InvokeAsync(CancellationToken cancellationToken) =>
        _runner.RunAsync(parameters.Pipeline, cancellationToken);
}

/// <summary>Lists the pipelines the application registered.</summary>
[Verb<ListPipelinesHandler>("list", Description = "Lists the pipelines this application can run.")]
public class ListPipelinesParams
{
}

/// <summary>Handles <c>list</c>.</summary>
public class ListPipelinesHandler : BaseHandler<ListPipelinesParams>
{
    private readonly PipelineRunner _runner;
    private readonly ILogger<ListPipelinesHandler> _logger;

    /// <summary>Takes the runner every command line front end shares.</summary>
    public ListPipelinesHandler(
        ParseResult result,
        ListPipelinesParams parameters,
        PipelineRunner runner,
        ILogger<ListPipelinesHandler> logger)
        : base(result, parameters)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task<int> InvokeAsync(CancellationToken cancellationToken)
    {
        var names = _runner.Pipelines.Select(pipeline => pipeline.Name).ToArray();

        if (names.Length == 0)
        {
            _logger.LogWarning("This application registered no pipelines.");
            return Task.FromResult(1);
        }

        // The writer rather than the log: this is the command's output, the thing a caller would pipe
        // into something else, and it should not be interleaved with diagnostics or prefixed.
        foreach (var name in names)
        {
            Writer.WriteLine(name);
        }

        return Task.FromResult(0);
    }
}
