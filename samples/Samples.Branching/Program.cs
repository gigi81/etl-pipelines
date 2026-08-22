using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Excel;
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
public static class Sample
{
    public const string PipelineName = "readings";

    public static async Task<PipelineResult> RunAsync(IDirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.Create();

        var incoming = directory.File("readings.csv");
        var archive = directory.File("archive.csv");
        var report = directory.File("report.xlsx");

        await WriteInputAsync(incoming);

        var services = new ServiceCollection();

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

        var result = await services.BuildServiceProvider()
            .GetRequiredEtlPipeline(PipelineName)
            .RunAsync(CancellationToken.None);

        return result.IsError
            ? throw new InvalidOperationException(result.FirstError.Description)
            : result.Value;
    }

    private static async Task WriteInputAsync(IFileInfo file)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<string> { "SensorId,TakenAt,Celsius" };

        for (var i = 0; i < 2_000; i++)
        {
            // A cold snap in the middle, so the filtered branch genuinely drops rows.
            var celsius = Math.Round(Math.Sin(i / 50.0) * 20, 2);
            lines.Add($"{i % 8},{start.AddMinutes(i):yyyy-MM-ddTHH:mm:ss},{celsius.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        await file.WriteAllLinesAsync(lines, CancellationToken.None);
    }

    public static async Task Main()
    {
        var fileSystem = new FileSystem();
        var directory = fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-sample-branch-{Guid.NewGuid():N}"));

        var result = await RunAsync(directory);

        Console.WriteLine($"read {result.RowsRead} once, wrote {result.RowsWritten} across both branches");
        Console.WriteLine($"archive: {directory.File("archive.csv").FullName}");
        Console.WriteLine($"report:  {directory.File("report.xlsx").FullName}");
    }
}
