using System.Data.Common;
using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.CsvToDatabase;

public sealed record Trade(int Id, string Symbol, decimal Price, int Quantity);

/// <summary>
/// Loading a file into a table, against whichever database is in front of you.
/// </summary>
/// <remarks>
/// The pipeline is the same for every engine: only the connection and the bulk loader change, which
/// is why they are the two things this sample takes as parameters. Run on its own it uses SQLite, and
/// the integration tests run this very method against SQL Server, PostgreSQL, MySQL and Oracle.
/// </remarks>
public static class Sample
{
    public const string PipelineName = "trades";

    public const string TableName = "trades";

    public static async Task<PipelineResult> RunAsync(
        IDirectoryInfo directory,
        Func<CancellationToken, ValueTask<DbConnection>> openConnection,
        IBulkLoader? bulkLoader = null,
        string? parameterPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.Create();

        var input = directory.File("trades.csv");
        await WriteInputAsync(input);

        var services = new ServiceCollection();

        // The provider's fast path, when the caller has one. Without it the sink falls back to
        // parameterised INSERTs, which behave the same everywhere but are slower.
        if (bulkLoader is not null)
        {
            services.AddSingleton(bulkLoader);
        }

        services.AddEtlPipeline(PipelineName, builder => builder
            .WithOptions(options => options.BatchSize = 1_000)
            .FromCsv<Trade>(input)
            .Where(trade => trade.Quantity > 0)
            .ToSqlTable(openConnection, TableName, options =>
            {
                if (parameterPrefix is not null)
                {
                    options.ParameterPrefix = parameterPrefix;
                }
            }));

        var result = await services.BuildServiceProvider()
            .GetRequiredEtlPipeline(PipelineName)
            .RunAsync(CancellationToken.None);

        return result.IsError
            ? throw new InvalidOperationException(result.FirstError.Description)
            : result.Value;
    }

    /// <summary>The rows this sample loads, and the count the tests expect to arrive.</summary>
    public const int ExpectedRows = 9_500;

    private static async Task WriteInputAsync(IFileInfo file)
    {
        var lines = new List<string>(10_001) { "Id,Symbol,Price,Quantity" };
        var symbols = new[] { "ACME", "GLBX", "INIT", "UMBR" };

        for (var i = 1; i <= 10_000; i++)
        {
            // Every twentieth row is a cancellation, which the pipeline filters out.
            var quantity = i % 20 == 0 ? 0 : (i % 500) + 1;
            lines.Add($"{i},{symbols[i % symbols.Length]},{(i % 1000) + 0.25m},{quantity}");
        }

        await file.WriteAllLinesAsync(lines, CancellationToken.None);
    }

    public static async Task Main()
    {
        var fileSystem = new FileSystem();
        var directory = fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-sample-load-{Guid.NewGuid():N}"));
        directory.Create();

        var connectionString = $"Data Source={directory.File("trades.db").FullName}";
        await CreateTableAsync(connectionString);

        var result = await RunAsync(directory, SqliteConnections.Open(connectionString));

        Console.WriteLine($"read {result.RowsRead}, loaded {result.RowsWritten} in {result.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"database: {directory.File("trades.db").FullName}");
    }

    private static async Task CreateTableAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE TABLE {TableName} (Id INTEGER, Symbol TEXT, Price NUMERIC, Quantity INTEGER)";
        await command.ExecuteNonQueryAsync();
    }
}
