using System.IO.Abstractions;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.CsvToExcel;

/// <summary>
/// Stands in for whatever normally drops the file: an export, an upload, a partner feed.
/// </summary>
/// <remarks>
/// A service rather than a static helper, so it takes the workspace and its logger the same way
/// everything else does — and so a test can leave it out and put its own file in place instead.
/// </remarks>
public sealed class SalesData
{
    private readonly SampleWorkspace _workspace;
    private readonly ILogger<SalesData> _logger;

    public SalesData(SampleWorkspace workspace, ILogger<SalesData> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(logger);

        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>Rows written, of which every tenth is a refund the pipeline filters out.</summary>
    public const int Rows = 500;

    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        var file = _workspace.File(SalesPipeline.InputFile);
        var lines = new List<string> { "Id,Region,Product,Amount" };

        for (var i = 1; i <= Rows; i++)
        {
            var amount = i % 10 == 0 ? -i : i * 1.25m;
            lines.Add($"{i},region-{i % 5},product-{i % 20},{amount}");
        }

        await file.WriteAllLinesAsync(lines, cancellationToken);
        _logger.LogInformation("Wrote {Rows} rows to {File}", Rows, file.FullName);
    }
}
