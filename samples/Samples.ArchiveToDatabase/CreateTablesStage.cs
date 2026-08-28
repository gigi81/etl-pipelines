using System.Diagnostics;
using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.ArchiveToDatabase;

/// <summary>
/// Creates the five destination tables in the sample's own SQLite database.
/// </summary>
/// <remarks>
/// A stage of its own, and one the pipeline only adds when it brought its own database: a caller that
/// registered another engine has already created the tables, in whatever dialect that engine wants.
/// </remarks>
public sealed class CreateTablesStage : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<CreateTablesStage> _logger;

    public CreateTablesStage(
        [FromKeyedServices(EtlPipelinesHost.WorkspaceKey)] IDirectoryInfo directory,
        ILogger<CreateTablesStage> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "create-tables";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        await using var connection = new SqliteConnection(Pipeline.ConnectionString(_directory));
        await connection.OpenAsync(cancellationToken);

        foreach (var table in Pipeline.Tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS {table}; CREATE TABLE {table} (Id INTEGER, Name TEXT)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Created {Count} tables", Pipeline.Tables.Length);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
