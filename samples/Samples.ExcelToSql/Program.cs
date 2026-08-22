using ErrorOr;
using System.Globalization;
using System.IO.Abstractions;
using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using EtlPipelines.Excel;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.ExcelToSql;

/// <summary>A row as the spreadsheet has it — which is to say, not necessarily valid.</summary>
public sealed class SubmittedOrder
{
    public int Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

/// <summary>Keeps whatever the workbook could not be read into, so nothing is lost silently.</summary>
public sealed class RejectedRows : IDeadLetterSink<string>
{
    private readonly List<string> _rows = [];

    public IReadOnlyList<string> Rows => _rows;

    public ValueTask WriteAsync(string row, Error error, CancellationToken cancellationToken)
    {
        _rows.Add(row);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The load half of the job: a spreadsheet someone filled in by hand goes into a table, in one
/// transaction, with the rows that would not convert kept aside rather than failing the load.
/// </summary>
public static class Sample
{
    public const string PipelineName = "import";

    public static async Task<(PipelineResult Result, int Rejected, long Loaded)> RunAsync(IDirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        directory.Create();

        var workbook = directory.File("submitted.xlsx");
        var database = directory.File("orders.db");
        var connectionString = $"Data Source={database.FullName}";

        await WriteWorkbookAsync(workbook);
        await CreateTableAsync(connectionString);

        var rejected = new RejectedRows();
        var open = SqliteConnections.Open(connectionString);

        var services = new ServiceCollection();

        // Registering the dead-letter sink is the whole of the configuration: FromExcel looks for one
        // in the container.
        services.AddSingleton<IDeadLetterSink<string>>(rejected);

        services.AddEtlPipeline(PipelineName, builder => builder
            .FromExcel<SubmittedOrder>(workbook)
            .Where(order => order.Amount > 0)
            .ToSqlTable(open, "orders", options => options.Columns = ["Id", "Customer", "Amount"]));

        var result = await services.BuildServiceProvider()
            .GetRequiredEtlPipeline(PipelineName)
            .RunAsync(CancellationToken.None);

        if (result.IsError)
        {
            throw new InvalidOperationException(result.FirstError.Description);
        }

        return (result.Value, rejected.Rows.Count, await CountAsync(connectionString));
    }

    /// <summary>A row as somebody typed it: the amount is text, and not all of it is a number.</summary>
    private sealed class TypedInByHand
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
    }

    /// <summary>A workbook with a few rows nobody could load, because that is what hand-filled files are like.</summary>
    private static async Task WriteWorkbookAsync(IFileInfo file)
    {
        var rows = Enumerable.Range(1, 200)
            .Select(i => new TypedInByHand
            {
                Id = i,
                Customer = $"customer-{i}",
                // Every fortieth row holds something that is not a number at all.
                Amount = i % 40 == 0
                    ? "not filled in"
                    : (i * 1.25m).ToString(CultureInfo.InvariantCulture),
            })
            .ToArray();

        var sink = new ExcelSink<TypedInByHand>(file);
        await sink.InitializeAsync(CancellationToken.None);
        await sink.WriteAsync(rows, CancellationToken.None);
        await sink.CompleteAsync(CancellationToken.None);
        await sink.DisposeAsync();
    }

    private static async Task CreateTableAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DROP TABLE IF EXISTS orders; CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orders";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public static async Task Main()
    {
        var fileSystem = new FileSystem();
        var directory = fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-sample-import-{Guid.NewGuid():N}"));

        var (result, rejected, loaded) = await RunAsync(directory);

        Console.WriteLine($"read {result.RowsRead}, loaded {loaded}, set aside {rejected} unreadable rows");
        Console.WriteLine($"database: {directory.File("orders.db").FullName}");
    }
}
