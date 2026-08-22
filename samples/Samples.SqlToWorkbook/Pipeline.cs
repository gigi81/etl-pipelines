using System.IO.Abstractions;
using EtlPipelines.Core;
using EtlPipelines.Excel;
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
public static class Pipeline
{
    /// <summary>The name the pipeline is registered under.</summary>
    public const string Name = "report";

    /// <summary>The database the queries run against, named once and referred to by name after that.</summary>
    public const string Connection = "sales";

    /// <summary>The workbook the run produces.</summary>
    public const string OutputFile = "report.xlsx";

    /// <summary>The connection string for the sample's own database, inside its workspace.</summary>
    public static string ConnectionString(IDirectoryInfo directory)
    {
        return $"Data Source={directory.File("sales.db").FullName}";
    }

    /// <summary>Registers the pipeline against the directory the run is working in.</summary>
    public static IServiceCollection AddPipeline(this IServiceCollection services, IDirectoryInfo directory)
    {
        // The overload taking the connection string, because this database is a file in a directory
        // chosen when the command ran. An application whose database has a fixed address would call
        // AddSqliteConnection("sales") instead and let it come from ConnectionStrings:sales.
        services.AddSqliteConnection(Connection, ConnectionString(directory));

        // One file object for every sheet: the workbook's sheet count is worked out while the
        // pipeline is composed, and it is keyed on the file it was given.
        var report = directory.File(OutputFile);

        // Three sources, three sheets, one workbook. Each From/To pair is its own stage, and stages
        // run one after another, which is the ordering a single workbook needs. No mapping delegates:
        // columns are matched to properties by name.
        return services.AddEtlPipeline(Name, builder => builder
            // The database this job reports on is filled by a step ahead of the queries.
            .AddStage<SeedStage>()
            .FromSql<Order>(Connection, "SELECT Id, Customer, Amount FROM orders ORDER BY Id")
            .ToExcelSheet(report, "Orders")
            .FromSql<RegionTotal>(Connection, "SELECT Region, Total FROM region_totals ORDER BY Region")
            .ToExcelSheet(report, "By region")
            .FromSql<Customer>(Connection, "SELECT Name, Country FROM customers ORDER BY Name")
            .ToExcelSheet(report, "Customers"));
    }
}
