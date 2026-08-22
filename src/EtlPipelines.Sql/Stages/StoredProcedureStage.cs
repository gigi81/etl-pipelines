using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace EtlPipelines.Sql.Stages;

/// <summary>
/// Runs a stored procedure as a stage of a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The other half of loading data: a stage that moves no rows through the framework but tells the
/// server to do something with the rows an earlier stage put there. Refreshing an aggregate,
/// swapping a staging table into place, letting a procedure do the work that is far cheaper next to
/// the data than round-tripped through this process.
/// </para>
/// <para>
/// Row counts are reported as zero, which is what makes it transparent to
/// <see cref="PipelineResult.RowsRead"/> and <see cref="PipelineResult.RowsWritten"/>: the rows the
/// procedure touched are the server's business and were never in a batch here. How many rows it
/// reported affecting is logged in the span instead.
/// </para>
/// <para>
/// No transaction is opened. A procedure that needs one generally manages its own, and wrapping one
/// from out here behaves differently on every engine - Oracle commits DDL implicitly whatever the
/// caller wanted.
/// </para>
/// </remarks>
public sealed class StoredProcedureStage : IPipelineStage
{
    private readonly string _connectionName;
    private readonly string _procedure;
    private readonly StoredProcedureOptions _options;

    /// <summary>Runs <paramref name="procedure"/> over the connection registered as <paramref name="connectionName"/>.</summary>
    public StoredProcedureStage(string connectionName, string procedure, StoredProcedureOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(procedure);

        _connectionName = connectionName;
        _procedure = procedure;
        _options = options ?? new StoredProcedureOptions();

        Name = options?.Name ?? procedure;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.StartNew();

        try
        {
            await using var connection = await context.Services
                .GetRequiredDbConnectionFactory(_connectionName)
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = _procedure;
            command.CommandType = CommandType.StoredProcedure;

            if (_options.CommandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            _options.Configure?.Invoke(command);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            return Error.Failure($"sql.procedure.{Name}.failed", $"{_procedure}: {exception.Message}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
