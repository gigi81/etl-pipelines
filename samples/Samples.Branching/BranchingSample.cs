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
public sealed class BranchingSample : Sample
{
    public override string PipelineName => "readings";

    public override string Description => "One read of the source, an archive and a report out of it";

    public override void Register(IServiceCollection services, SampleWorkspace workspace)
    {
        var incoming = workspace.File("readings.csv");
        var archive = workspace.File("archive.csv");
        var report = workspace.File("report.xlsx");

        services.AddEtlPipeline(PipelineName, builder => builder
            .WithOptions(options => options.BatchSize = 256)
            .FromCsv<Reading>(incoming)
            .Branch(
                // Untouched, exactly as it arrived.
                archived => archived.ToCsv(archive),
                // The same rows, on their way to something a person will read.
                reported => reported
                    .Where(reading => reading.Celsius > 0)
                    .Select(reading => new ReadingReport
                    {
                        SensorId = reading.SensorId,
                        TakenAt = reading.TakenAt,
                        Fahrenheit = Math.Round((reading.Celsius * 9 / 5) + 32, 2),
                    })
                    .ToExcel(report)));
    }

    public override Task PrepareAsync(SampleWorkspace workspace, CancellationToken cancellationToken) =>
        SampleData.WriteReadingsAsync(workspace.File("readings.csv"), cancellationToken);
}
