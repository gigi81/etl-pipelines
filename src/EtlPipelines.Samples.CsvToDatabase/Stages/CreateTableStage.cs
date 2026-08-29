using System.Diagnostics;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Sql;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.CsvToDatabase.Stages;

/// <summary>
/// Creates the destination table in the sample's own database.
/// </summary>
/// <remarks>
/// A stage of its own, and one the pipeline only adds when it brought its own database: a caller that
/// registered another engine has already created the table, in whatever dialect that engine wants.
/// Conditionally adding a step is the ordinary way to say that.
/// </remarks>
public sealed class CreateTableStage : IPipelineStage
{
    private readonly ILogger<CreateTableStage> _logger;

    public CreateTableStage(ILogger<CreateTableStage> logger)
    {
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

        // Resolved from the run's own connection, the same way TruncateTableStage does - whichever
        // engine AddPipeline's caller registered under Pipeline.Connection, not SQLite by assumption.
        await using var connection = await context.Services
            .GetRequiredDbConnectionFactory(Pipeline.Connection)
            .OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {Pipeline.Table}; " +
            $"CREATE TABLE {Pipeline.Table} (Id INTEGER, Symbol TEXT, Price NUMERIC, Quantity INTEGER)";

        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Created {Table}", Pipeline.Table);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
