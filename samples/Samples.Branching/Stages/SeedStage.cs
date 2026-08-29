using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.Branching.Stages;

/// <summary>Stands in for the sensor feed this job would normally be reading.</summary>
public sealed class SeedStage : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<SeedStage> _logger;

    public SeedStage(
        [FromKeyedServices(EtlPipelinesHost.WorkspaceKey)] IDirectoryInfo directory,
        ILogger<SeedStage> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>Rows written. A cold snap in the middle makes the filtered branch drop some.</summary>
    public const int Rows = 2_000;

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "fetch";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        var file = _directory.File(Pipeline.InputFile);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<string> { "SensorId,TakenAt,Celsius" };

        for (var i = 0; i < Rows; i++)
        {
            var celsius = Math.Round(Math.Sin(i / 50.0) * 20, 2);
            lines.Add($"{i % 8},{start.AddMinutes(i):yyyy-MM-ddTHH:mm:ss},{celsius.ToString(CultureInfo.InvariantCulture)}");
        }

        await file.WriteAllLinesAsync(lines, cancellationToken);
        _logger.LogInformation("Wrote {Rows} readings to {File}", Rows, file.FullName);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
