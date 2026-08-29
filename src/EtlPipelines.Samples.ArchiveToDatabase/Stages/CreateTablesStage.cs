using System.Diagnostics;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Extensions.Sql;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.ArchiveToDatabase.Stages;

/// <summary>
/// Creates the five destination tables in the sample's own database.
/// </summary>
/// <remarks>
/// A stage of its own, and one the pipeline only adds when it brought its own database: a caller that
/// registered another engine has already created the tables, in whatever dialect that engine wants.
/// </remarks>
public sealed class CreateTablesStage : IPipelineStage
{
    private readonly ILogger<CreateTablesStage> _logger;

    public CreateTablesStage(ILogger<CreateTablesStage> logger)
    {
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

        // Resolved from the run's own connection, the same way TruncateTableStage does - whichever
        // engine AddPipeline's caller registered under Pipeline.Connection, not SQLite by assumption.
        await using var connection = await context.Services
            .GetRequiredDbConnectionFactory(Pipeline.Connection)
            .OpenAsync(cancellationToken);

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
