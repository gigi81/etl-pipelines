using System.Globalization;
using System.IO.Abstractions;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.Branching;

/// <summary>Stands in for the sensor feed this job would normally be reading.</summary>
public sealed class ReadingsData
{
    private readonly SampleWorkspace _workspace;
    private readonly ILogger<ReadingsData> _logger;

    public ReadingsData(SampleWorkspace workspace, ILogger<ReadingsData> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(logger);

        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>Rows written. A cold snap in the middle makes the filtered branch drop some.</summary>
    public const int Rows = 2_000;

    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        var file = _workspace.File(ReadingsPipeline.InputFile);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<string> { "SensorId,TakenAt,Celsius" };

        for (var i = 0; i < Rows; i++)
        {
            var celsius = Math.Round(Math.Sin(i / 50.0) * 20, 2);
            lines.Add($"{i % 8},{start.AddMinutes(i):yyyy-MM-ddTHH:mm:ss},{celsius.ToString(CultureInfo.InvariantCulture)}");
        }

        await file.WriteAllLinesAsync(lines, cancellationToken);
        _logger.LogInformation("Wrote {Rows} readings to {File}", Rows, file.FullName);
    }
}
