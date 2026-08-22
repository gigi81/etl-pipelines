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

    /// <summary>The database this sample reports on, named once and referred to by name after that.</summary>
    public const string Connection = "sales";

    public override IEnumerable<KeyValuePair<string, string?>> ConnectionStrings(SampleWorkspace workspace) =>
        [new(Connection, $"Data Source={workspace.File("sales.db").FullName}")];

    public override void Register(IServiceCollection services, SampleWorkspace workspace)
    {
        var report = workspace.File("report.xlsx");

        // The connection string comes from configuration, under ConnectionStrings:sales.
        services.AddSqliteConnection(Connection);

        // Three sources, three sheets, one workbook. Each From/To pair is its own stage, and stages
        // run one after another, which is the ordering a single workbook needs.
        //
        // No mapping delegate: columns are matched to properties by name, by the compiled parser
        // Dapper builds once the query's columns are known.
        services.AddEtlPipeline(PipelineName, builder => builder
            .FromSql<Order>(Connection, "SELECT Id, Customer, Amount FROM orders ORDER BY Id")
            .ToExcelSheet(report, "Orders")
            .FromSql<RegionTotal>(Connection, "SELECT Region, Total FROM region_totals ORDER BY Region")
            .ToExcelSheet(report, "By region")
            .FromSql<Customer>(Connection, "SELECT Name, Country FROM customers ORDER BY Name")
            .ToExcelSheet(report, "Customers"));
    }

    public override Task PrepareAsync(SampleWorkspace workspace, CancellationToken cancellationToken) =>
        SampleData.SeedAsync($"Data Source={workspace.File("sales.db").FullName}", cancellationToken);
}
