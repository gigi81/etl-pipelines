using ErrorOr;
using System.Diagnostics;
using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.CsvToExcel;

/// <summary>
/// Stands in for whatever normally drops the file: an export, an upload, a partner feed.
/// </summary>
/// <remarks>
/// A service rather than a static helper, so it takes the workspace and its logger the same way
/// everything else does — and so a test can leave it out and put its own file in place instead.
/// </remarks>
public sealed class SalesData : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<SalesData> _logger;

    public SalesData(
        [FromKeyedServices(SampleWorkspace.Key)] IDirectoryInfo directory,
        ILogger<SalesData> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>Rows written, of which every tenth is a refund the pipeline filters out.</summary>
    public const int Rows = 500;

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "fetch";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        var file = _directory.File(SalesPipeline.InputFile);
        var lines = new List<string> { "Id,Region,Product,Amount" };

        for (var i = 1; i <= Rows; i++)
        {
            var amount = i % 10 == 0 ? -i : i * 1.25m;
            lines.Add($"{i},region-{i % 5},product-{i % 20},{amount}");
        }

        await file.WriteAllLinesAsync(lines, cancellationToken);
        _logger.LogInformation("Wrote {Rows} rows to {File}", Rows, file.FullName);

        // No rows in or out: what this step wrote is a file, not rows through the framework, and
        // a stage that reports none is skipped when the run's totals are worked out.
        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
