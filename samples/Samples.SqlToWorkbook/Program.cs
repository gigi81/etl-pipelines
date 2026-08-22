using System.Data;
using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using EtlPipelines.Excel;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Data.Sqlite;
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
public static class Sample
{
    public const string PipelineName = "report";

    public static async Task<PipelineResult> RunAsync(IDirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.Create();

        var database = directory.File("sales.db");
        var report = directory.File("report.xlsx");
        var connectionString = $"Data Source={database.FullName}";

        await SeedAsync(connectionString);

        var open = SqliteConnections.Open(connectionString);
        var services = new ServiceCollection();

        // Three sources, three sheets, one workbook. Each From/To pair is its own stage, and stages
        // run one after another, which is the ordering a single workbook needs.
        services.AddEtlPipeline(PipelineName, builder => builder
            .FromSql(open, "SELECT Id, Customer, Amount FROM orders ORDER BY Id", ReadOrder)
            .ToExcelSheet(report, "Orders")
            .FromSql(open, "SELECT Region, Total FROM region_totals ORDER BY Region", ReadRegionTotal)
            .ToExcelSheet(report, "By region")
            .FromSql(open, "SELECT Name, Country FROM customers ORDER BY Name", ReadCustomer)
            .ToExcelSheet(report, "Customers"));

        var result = await services.BuildServiceProvider()
            .GetRequiredEtlPipeline(PipelineName)
            .RunAsync(CancellationToken.None);

        return result.IsError
            ? throw new InvalidOperationException(result.FirstError.Description)
            : result.Value;
    }

    private static Order ReadOrder(IDataRecord r) =>
        new() { Id = r.GetInt64(0), Customer = r.GetString(1), Amount = r.GetDecimal(2) };

    private static RegionTotal ReadRegionTotal(IDataRecord r) =>
        new() { Region = r.GetString(0), Total = r.GetDecimal(1) };

    private static Customer ReadCustomer(IDataRecord r) =>
        new() { Name = r.GetString(0), Country = r.GetString(1) };

    /// <summary>Stands in for the database a real job would already be pointed at.</summary>
    private static async Task SeedAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS orders;
            DROP TABLE IF EXISTS customers;
            DROP TABLE IF EXISTS region_totals;

            CREATE TABLE orders (Id INTEGER PRIMARY KEY, Customer TEXT, Amount NUMERIC);
            CREATE TABLE customers (Name TEXT, Country TEXT);
            CREATE TABLE region_totals (Region TEXT, Total NUMERIC);

            INSERT INTO orders (Customer, Amount)
            SELECT 'customer-' || value, value * 1.5 FROM generate_series;

            INSERT INTO customers (Name, Country)
            SELECT 'customer-' || value, CASE value % 3 WHEN 0 THEN 'IT' WHEN 1 THEN 'UK' ELSE 'FR' END
            FROM generate_series;

            INSERT INTO region_totals (Region, Total)
            SELECT 'region-' || (value % 5), SUM(value * 1.5) FROM generate_series GROUP BY value % 5;
            """;

        // SQLite has no generate_series by default in this provider build, so the rows are made with
        // a recursive CTE instead.
        command.CommandText = command.CommandText.Replace(
            "FROM generate_series",
            "FROM (WITH RECURSIVE series(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM series WHERE value < 200) SELECT value FROM series)");

        await command.ExecuteNonQueryAsync();
    }

    public static async Task Main()
    {
        var fileSystem = new FileSystem();
        var directory = fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-sample-sql-{Guid.NewGuid():N}"));

        var result = await RunAsync(directory);

        Console.WriteLine($"{result.Stages.Count} sheets written in {result.Elapsed.TotalMilliseconds:F0} ms");
        foreach (var stage in result.Stages)
        {
            Console.WriteLine($"  {stage.Name}: {stage.RowsIn} rows");
        }

        Console.WriteLine($"workbook: {directory.File("report.xlsx").FullName}");
    }
}
