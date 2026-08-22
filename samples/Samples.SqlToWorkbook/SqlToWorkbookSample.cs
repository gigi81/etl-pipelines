using System.Data;
using EtlPipelines.Core;
using EtlPipelines.Excel;
using EtlPipelines.Samples.Common;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.SqlToWorkbook;

public sealed class Order
{
    public long Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class RegionTotal
{
    public string Region { get; set; } = string.Empty;
    public decimal Total { get; set; }
}

public sealed class Customer
{
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

/// <summary>
/// The reporting job: several queries against one database, each becoming a sheet of a single
/// workbook that only appears once every sheet has been written.
/// </summary>
public sealed class SqlToWorkbookSample : Sample
{
    public override string PipelineName => "report";

    public override string Description => "Three queries, three sheets, one workbook";

    public override void Register(IServiceCollection services, SampleWorkspace workspace)
    {
        var report = workspace.File("report.xlsx");
        var open = SqliteConnections.Open(ConnectionString(workspace));

        // Three sources, three sheets, one workbook. Each From/To pair is its own stage, and stages
        // run one after another, which is the ordering a single workbook needs.
        services.AddEtlPipeline(PipelineName, builder => builder
            .FromSql(open, "SELECT Id, Customer, Amount FROM orders ORDER BY Id", ReadOrder)
            .ToExcelSheet(report, "Orders")
            .FromSql(open, "SELECT Region, Total FROM region_totals ORDER BY Region", ReadRegionTotal)
            .ToExcelSheet(report, "By region")
            .FromSql(open, "SELECT Name, Country FROM customers ORDER BY Name", ReadCustomer)
            .ToExcelSheet(report, "Customers"));
    }

    public override Task PrepareAsync(SampleWorkspace workspace, CancellationToken cancellationToken) =>
        SampleData.SeedAsync(ConnectionString(workspace), cancellationToken);

    /// <summary>Stands in for the database a real job would already be pointed at.</summary>
    internal static string ConnectionString(SampleWorkspace workspace) =>
        $"Data Source={workspace.File("sales.db").FullName}";

    private static Order ReadOrder(IDataRecord r) =>
        new() { Id = r.GetInt64(0), Customer = r.GetString(1), Amount = r.GetDecimal(2) };

    private static RegionTotal ReadRegionTotal(IDataRecord r) =>
        new() { Region = r.GetString(0), Total = r.GetDecimal(1) };

    private static Customer ReadCustomer(IDataRecord r) =>
        new() { Name = r.GetString(0), Country = r.GetString(1) };
}
