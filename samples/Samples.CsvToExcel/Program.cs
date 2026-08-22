using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Excel;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.CsvToExcel;

/// <summary>A row as it appears in the incoming file.</summary>
public sealed record SalesRow(int Id, string Region, string Product, decimal Amount);

/// <summary>
/// A row as it appears in the workbook. Written by the Excel sink and read back by its source, so it
/// needs a parameterless constructor and settable properties.
/// </summary>
public sealed class SalesReport
{
    public int Id { get; set; }
    public string Region { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public decimal AmountInCents { get; set; }
}

/// <summary>
/// The most ordinary ETL job there is: read a CSV, drop the rows that do not belong, reshape the
/// rest, write a spreadsheet.
/// </summary>
public static class Sample
{
    public const string PipelineName = "sales";

    /// <summary>Runs the sample against <paramref name="directory"/> and returns what the run did.</summary>
    public static async Task<PipelineResult> RunAsync(IDirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.Create();

        var input = directory.File("sales.csv");
        var output = directory.File("sales.xlsx");

        await WriteInputAsync(input);

        var services = new ServiceCollection();

        services.AddEtlPipeline(PipelineName, builder => builder
            .FromCsv<SalesRow>(input)
            .Where(row => row.Amount > 0)
            .Select(row => new SalesReport
            {
                Id = row.Id,
                Region = row.Region,
                Product = row.Product,
                AmountInCents = row.Amount * 100,
            })
            .ToExcel(output));

        var result = await services.BuildServiceProvider()
            .GetRequiredEtlPipeline(PipelineName)
            .RunAsync(CancellationToken.None);

        return result.IsError
            ? throw new InvalidOperationException(result.FirstError.Description)
            : result.Value;
    }

    /// <summary>Stands in for whatever normally drops the file: an export, an upload, a partner feed.</summary>
    private static async Task WriteInputAsync(IFileInfo file)
    {
        var lines = new List<string> { "Id,Region,Product,Amount" };

        for (var i = 1; i <= 500; i++)
        {
            // Every tenth row is a refund, which the pipeline filters out.
            var amount = i % 10 == 0 ? -i : i * 1.25m;
            lines.Add($"{i},region-{i % 5},product-{i % 20},{amount}");
        }

        await file.WriteAllLinesAsync(lines, CancellationToken.None);
    }

    public static async Task Main()
    {
        var fileSystem = new FileSystem();
        var directory = fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-sample-csv-{Guid.NewGuid():N}"));

        var result = await RunAsync(directory);

        Console.WriteLine($"read {result.RowsRead}, wrote {result.RowsWritten} in {result.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"workbook: {directory.File("sales.xlsx").FullName}");
    }
}
