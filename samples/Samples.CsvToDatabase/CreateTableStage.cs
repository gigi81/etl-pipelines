using System.Diagnostics;
using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.CsvToDatabase;

/// <summary>
/// Creates the destination table in the sample's own SQLite database.
/// </summary>
/// <remarks>
/// A stage of its own, and one the pipeline only adds when it brought its own database: a caller that
/// registered another engine has already created the table, in whatever dialect that engine wants.
/// Conditionally adding a step is the ordinary way to say that.
/// </remarks>
public sealed class CreateTableStage : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<CreateTableStage> _logger;

    public CreateTableStage(
        [FromKeyedServices(EtlPipelinesHost.WorkspaceKey)] IDirectoryInfo directory,
        ILogger<CreateTableStage> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "create-table";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        await using var connection = new SqliteConnection(Pipeline.ConnectionString(_directory));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {Pipeline.Table}; " +
            $"CREATE TABLE {Pipeline.Table} (Id INTEGER, Symbol TEXT, Price NUMERIC, Quantity INTEGER)";

        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Created {Table}", Pipeline.Table);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
