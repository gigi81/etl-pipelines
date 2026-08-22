using System.CommandLine;
using Albatross.CommandLine;
using EtlPipelines.Hosting.Commands;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Hosting.Handlers;

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
