using System.Diagnostics;
using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.ExcelToSql;

/// <summary>Reports what the load left behind, once every row has been through.</summary>
public sealed class CheckStage : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly RejectedRows _rejected;
    private readonly ILogger<CheckStage> _logger;

    public CheckStage(
        [FromKeyedServices(EtlPipelinesHost.WorkspaceKey)] IDirectoryInfo directory,
        RejectedRows rejected,
        ILogger<CheckStage> logger)
    {
        _directory = directory;
        _rejected = rejected;
        _logger = logger;
    }

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "check";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        _logger.LogInformation(
            "loaded {Loaded} rows, set aside {Rejected} that could not be read",
            await Pipeline.CountAsync(_directory, cancellationToken),
            _rejected.Rows.Count);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
