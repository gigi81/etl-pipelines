using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Excel;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.Branching;

public sealed record Reading(int SensorId, DateTime TakenAt, double Celsius);

public sealed class ReadingReport
{
    public int SensorId { get; set; }
    public DateTime TakenAt { get; set; }
    public double Fahrenheit { get; set; }
}

/// <summary>
/// Keeping the raw record while the same rows carry on: the archive and the report come from one
/// pass over the source rather than from two jobs that have to agree with each other.
/// </summary>
public static class ReadingsPipeline
{
    /// <summary>The name the pipeline is registered under.</summary>
    public const string Name = "readings";

    /// <summary>The incoming file, as the sample's own seeder writes it.</summary>
    public const string InputFile = "readings.csv";

    /// <summary>Every row, exactly as it arrived.</summary>
    public const string ArchiveFile = "archive.csv";

    /// <summary>The warm rows, on their way to somebody who will read them.</summary>
    public const string ReportFile = "report.xlsx";

    /// <summary>Registers the pipeline against the directory the run is working in.</summary>
    public static IServiceCollection AddReadingsPipeline(this IServiceCollection services, SampleWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(workspace);

        return services.AddEtlPipeline(Name, builder => builder
            .WithOptions(options => options.BatchSize = 256)
            .FromCsv<Reading>(workspace.File(InputFile))
            .Branch(
                // Untouched, exactly as it arrived.
                archived => archived.ToCsv(workspace.File(ArchiveFile)),
                // The same rows, on their way to something a person will read.
                reported => reported
                    .Where(reading => reading.Celsius > 0)
                    .Select(reading => new ReadingReport
                    {
                        SensorId = reading.SensorId,
                        TakenAt = reading.TakenAt,
                        Fahrenheit = Math.Round((reading.Celsius * 9 / 5) + 32, 2),
                    })
                    .ToExcel(workspace.File(ReportFile))));
    }
}
