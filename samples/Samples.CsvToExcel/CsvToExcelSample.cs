using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Excel;
using EtlPipelines.Samples.Common;
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
public sealed class CsvToExcelSample : Sample
{
    public override string PipelineName => "sales";

    public override string Description => "CSV in, filtered and reshaped, Excel out";

    public override void Register(IServiceCollection services, SampleWorkspace workspace)
    {
        var input = workspace.File("sales.csv");
        var output = workspace.File("sales.xlsx");

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
    }

    public override Task PrepareAsync(SampleWorkspace workspace, CancellationToken cancellationToken) =>
        SampleData.WriteSalesAsync(workspace.File("sales.csv"), cancellationToken);
}
