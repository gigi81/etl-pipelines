using System.Diagnostics;
using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.SqlToWorkbook;

/// <summary>
/// Fills the database this sample reports on.
/// </summary>
/// <remarks>Stands in for the database a real job would already be pointed at.</remarks>
public sealed class SeedStage : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<SeedStage> _logger;

    public SeedStage(
        [FromKeyedServices(EtlPipelinesHost.WorkspaceKey)] IDirectoryInfo directory,
        ILogger<SeedStage> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>Rows seeded into each table.</summary>
    public const int Rows = 200;

    /// <summary>This provider build has no <c>generate_series</c>, so the rows come from a recursive CTE.</summary>
    private const string Series =
        "(WITH RECURSIVE series(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM series WHERE value < 200) SELECT value FROM series)";

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "seed";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        await using var connection = new SqliteConnection(Pipeline.ConnectionString(_directory));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP TABLE IF EXISTS orders;
            DROP TABLE IF EXISTS customers;
            DROP TABLE IF EXISTS region_totals;

            CREATE TABLE orders (Id INTEGER PRIMARY KEY, Customer TEXT, Amount NUMERIC);
            CREATE TABLE customers (Name TEXT, Country TEXT);
            CREATE TABLE region_totals (Region TEXT, Total NUMERIC);

            INSERT INTO orders (Customer, Amount)
            SELECT 'customer-' || value, value * 1.5 FROM {Series};

            INSERT INTO customers (Name, Country)
            SELECT 'customer-' || value, CASE value % 3 WHEN 0 THEN 'IT' WHEN 1 THEN 'UK' ELSE 'FR' END
            FROM {Series};

            INSERT INTO region_totals (Region, Total)
            SELECT 'region-' || (value % 5), SUM(value * 1.5) FROM {Series} GROUP BY value % 5;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Seeded {Rows} orders and customers", Rows);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
