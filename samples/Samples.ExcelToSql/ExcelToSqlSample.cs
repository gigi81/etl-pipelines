using ErrorOr;
using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Core;
using EtlPipelines.Excel;
using EtlPipelines.Samples.Common;
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

/// <summary>
/// The load half of the job: a spreadsheet someone filled in by hand goes into a table, in one
/// transaction, with the rows that would not convert kept aside rather than failing the load.
/// </summary>
public sealed class ExcelToSqlSample : Sample
{
    public override string PipelineName => "import";

    public override string Description => "A hand-filled workbook into a table, bad rows set aside";

    /// <summary>The database the workbook is loaded into.</summary>
    public const string Connection = "orders";

    public override IEnumerable<KeyValuePair<string, string?>> ConnectionStrings(SampleWorkspace workspace) =>
        [new(Connection, ConnectionString(workspace))];

    public override void Register(IServiceCollection services, SampleWorkspace workspace)
    {
        var workbook = workspace.File("submitted.xlsx");

        // The connection string comes from configuration, under ConnectionStrings:orders.
        services.AddSqliteConnection(Connection);

        // Registering the dead-letter sink is the whole of the configuration: FromExcel looks for one
        // in the container. Without it a row that will not convert fails the run.
        services.AddSingleton<RejectedRows>();
        services.AddSingleton<IDeadLetterSink<string>>(provider => provider.GetRequiredService<RejectedRows>());

        services.AddEtlPipeline(PipelineName, builder => builder
            .FromExcel<SubmittedOrder>(workbook)
            .Where(order => order.Amount > 0)
            .ToSqlTable(Connection, "orders", options => options.Columns = ["Id", "Customer", "Amount"]));
    }

    public async override Task PrepareAsync(SampleWorkspace workspace, CancellationToken cancellationToken)
    {
        await SampleData.WriteWorkbookAsync(workspace.File("submitted.xlsx"), cancellationToken);
        await SampleData.CreateTableAsync(ConnectionString(workspace), cancellationToken);
    }

    /// <summary>Row counts alone would not say that anything had been set aside.</summary>
    public async override Task ReportAsync(SampleOutcome outcome, CancellationToken cancellationToken)
    {
        var rejected = outcome.Services.GetRequiredService<RejectedRows>();

        outcome.Logger.LogInformation(
            "loaded {Loaded} rows, set aside {Rejected} that could not be read",
            await SampleData.CountAsync(ConnectionString(outcome.Workspace), cancellationToken),
            rejected.Rows.Count);
    }

    internal static string ConnectionString(SampleWorkspace workspace) =>
        $"Data Source={workspace.File("orders.db").FullName}";
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
