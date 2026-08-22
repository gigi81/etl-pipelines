using ErrorOr;
using System.Diagnostics;
using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Samples.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.CsvToDatabase;

/// <summary>
/// The file this sample loads, and the SQLite table it loads into when nobody supplies one.
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

    /// <summary>Rows written, of which every twentieth is a cancellation the pipeline filters out.</summary>
    public const int Rows = 10_000;

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "fetch";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        var file = _directory.File(Pipeline.InputFile);
        var lines = new List<string>(Rows + 1) { "Id,Symbol,Price,Quantity" };
        var symbols = new[] { "ACME", "GLBX", "INIT", "UMBR" };

        for (var i = 1; i <= Rows; i++)
        {
            var quantity = i % 20 == 0 ? 0 : (i % 500) + 1;
            lines.Add($"{i},{symbols[i % symbols.Length]},{(i % 1000) + 0.25m},{quantity}");
        }

        await file.WriteAllLinesAsync(lines, cancellationToken);
        _logger.LogInformation("Wrote {Rows} trades to {File}", Rows, file.FullName);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
