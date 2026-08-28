using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Configuration;
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
public static class Pipeline
{
    /// <summary>The name the pipeline is registered under.</summary>
    public const string Name = "import";

    /// <summary>The database the workbook is loaded into.</summary>
    public const string Connection = "orders";

    /// <summary>The table rows land in.</summary>
    public const string Table = "orders";

    /// <summary>The workbook the sample's seeder fills in.</summary>
    public const string InputFile = "submitted.xlsx";

    /// <summary>The connection string for the sample's own database, inside its workspace.</summary>
    public static string ConnectionString(IDirectoryInfo directory)
    {
        return $"Data Source={directory.File("orders.db").FullName}";
    }

    /// <summary>Counts what actually reached the table, which the row counts alone would not say.</summary>
    public static async Task<long> CountAsync(IDirectoryInfo directory, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString(directory));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Table}";

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>Registers the pipeline against the directory the run is working in.</summary>
    public static IServiceCollection AddPipeline(this IServiceCollection services, IDirectoryInfo workspace)
    {
        services.AddSqliteConnection(Connection, ConnectionString(workspace));

        // Registering the dead-letter sink is the whole of the configuration: FromExcel looks for one
        // in the container. Without it a row that will not convert fails the run. Registered under
        // both types so the command can read back what was set aside.
        services.AddSingleton<RejectedRows>();
        services.AddSingleton<IDeadLetterSink<string>>(provider => provider.GetRequiredService<RejectedRows>());

        return services.AddEtlPipeline(Name, builder => builder
            // The workbook arrives, and the table it lands in is created, before any row moves.
            .AddStage<SeedStage>()
            .FromExcel<SubmittedOrder>(workspace.File(InputFile))
            .Where(order => order.Amount > 0)
            .ToSqlTable(Connection, Table, options => options.Columns = ["Id", "Customer", "Amount"])
            // And a step after it, to say what the row counts cannot: how much of the workbook
            // nobody could read.
            .AddStage<CheckStage>());
    }
}
