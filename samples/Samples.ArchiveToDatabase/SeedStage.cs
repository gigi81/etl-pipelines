using System.Diagnostics;
using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.ArchiveToDatabase;

/// <summary>
/// Writes the five CSV files this sample bundles up, standing in for whatever produced them before
/// they reached this job - a nightly export, most likely, from five different tables of its own.
/// </summary>
public sealed class SeedStage : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<SeedStage> _logger;

    public SeedStage(
        [FromKeyedServices(SampleWorkspace.Key)] IDirectoryInfo directory,
        ILogger<SeedStage> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>Rows written to each of the five files.</summary>
    public const int RowsPerFile = 5;

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "seed";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var source = _directory.SubDirectory(Pipeline.SourceDirectory);
        source.Create();

        foreach (var table in Pipeline.Tables)
        {
            var file = source.File($"{table}.csv");
            var lines = new List<string>(RowsPerFile + 1) { "Id,Name" };

            for (var i = 1; i <= RowsPerFile; i++)
            {
                lines.Add($"{i},{table}-{i}");
            }

            await file.WriteAllLinesAsync(lines, cancellationToken);
        }

        _logger.LogInformation("Wrote {Count} CSV files to {Directory}", Pipeline.Tables.Length, source.FullName);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
