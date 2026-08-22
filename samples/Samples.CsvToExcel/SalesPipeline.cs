using System.IO.Abstractions;
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
public static class SalesPipeline
{
    /// <summary>The name the pipeline is registered under, and what <c>run</c> and <c>list</c> call it.</summary>
    public const string Name = "sales";

    /// <summary>The incoming file, as the sample's own seeder writes it.</summary>
    public const string InputFile = "sales.csv";

    /// <summary>The workbook the run produces.</summary>
    public const string OutputFile = "sales.xlsx";

    /// <summary>Registers the pipeline against the directory the run is working in.</summary>
    public static IServiceCollection AddSalesPipeline(this IServiceCollection services, IDirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(directory);

        return services.AddEtlPipeline(Name, builder => builder
            .FromCsv<SalesRow>(directory.File(InputFile))
            .Where(row => row.Amount > 0)
            .Select(row => new SalesReport
            {
                Id = row.Id,
                Region = row.Region,
                Product = row.Product,
                AmountInCents = row.Amount * 100,
            })
            .ToExcel(directory.File(OutputFile)));
    }
}
