using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Configuration;
using System.Diagnostics;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using EtlPipelines.Samples.Common;
using Microsoft.Data.Sqlite;
using EtlPipelines.Excel;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
public static class ImportPipeline
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
    public static IServiceCollection AddImportPipeline(this IServiceCollection services, IDirectoryInfo directory)
    {
        services.AddSqliteConnection(Connection, ConnectionString(directory));

        // Registering the dead-letter sink is the whole of the configuration: FromExcel looks for one
        // in the container. Without it a row that will not convert fails the run. Registered under
        // both types so the command can read back what was set aside.
        services.AddSingleton<RejectedRows>();
        services.AddSingleton<IDeadLetterSink<string>>(provider => provider.GetRequiredService<RejectedRows>());

        return services.AddEtlPipeline(Name, builder => builder
            // The workbook arrives, and the table it lands in is created, before any row moves.
            .AddStage<ImportData>()
            .FromExcel<SubmittedOrder>(directory.File(InputFile))
            .Where(order => order.Amount > 0)
            .ToSqlTable(Connection, Table, options => options.Columns = ["Id", "Customer", "Amount"])
            // And a step after it, to say what the row counts cannot: how much of the workbook
            // nobody could read.
            .AddStage<ImportCheck>());
    }
}

/// <summary>Reports what the load left behind, once every row has been through.</summary>
public sealed class ImportCheck : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly RejectedRows _rejected;
    private readonly ILogger<ImportCheck> _logger;

    public ImportCheck(
        [FromKeyedServices(SampleWorkspace.Key)] IDirectoryInfo directory,
        RejectedRows rejected,
        ILogger<ImportCheck> logger)
    {
        _directory = directory;
        _rejected = rejected;
        _logger = logger;
    }

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "check";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        _logger.LogInformation(
            "loaded {Loaded} rows, set aside {Rejected} that could not be read",
            await ImportPipeline.CountAsync(_directory, cancellationToken),
            _rejected.Rows.Count);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
