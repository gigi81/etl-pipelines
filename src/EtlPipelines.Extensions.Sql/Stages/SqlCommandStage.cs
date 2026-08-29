using System.Data;
using System.Data.Common;
using System.Diagnostics;

namespace EtlPipelines.Extensions.Sql.Stages;

/// <summary>
/// Runs one command — a statement or a stored procedure — as a stage of a pipeline.
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
/// server touched were never in a batch here.
/// </para>
/// <para>
/// One command, sent as given. Nothing is split on <c>GO</c> or on a changed delimiter — that is
/// <see cref="SqlScriptStage"/>'s business, and what a file of statements is for.
/// </para>
/// <para>
/// No transaction is opened. A procedure that needs one generally manages its own, and wrapping one
/// from out here behaves differently on every engine - Oracle commits DDL implicitly whatever the
/// caller wanted.
/// </para>
/// </remarks>
public sealed class SqlCommandStage : IPipelineStage
{
    private readonly string _connectionName;
    private readonly string _commandText;
    private readonly CommandType _commandType;
    private readonly SqlCommandOptions _options;

    /// <summary>Runs <paramref name="commandText"/> over the connection registered as <paramref name="connectionName"/>.</summary>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="commandText">The statement to run, or the name of the procedure to call.</param>
    /// <param name="commandType">Which of the two <paramref name="commandText"/> is.</param>
    /// <param name="options">Command timeout, parameters, and what the stage is called.</param>
    public SqlCommandStage(
        string connectionName,
        string commandText,
        CommandType commandType = CommandType.Text,
        SqlCommandOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        _connectionName = connectionName;
        _commandText = commandText;
        _commandType = commandType;
        _options = options ?? new SqlCommandOptions();

        Name = options?.Name ?? Describe(commandText, commandType);
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
            command.CommandText = _commandText;
            command.CommandType = _commandType;

            if (_options.CommandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            _options.Configure?.Invoke(command);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            var kind = _commandType == CommandType.StoredProcedure ? "procedure" : "command";
            return Error.Failure($"sql.{kind}.{Name}.failed", $"{_commandText}: {exception.Message}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    /// <summary>
    /// What the stage is called when the caller did not say.
    /// </summary>
    /// <remarks>
    /// A procedure has a name already. A statement does not, and "sql" three times over in a run's
    /// report tells nobody which one was slow — so it is called after its own first few words.
    /// </remarks>
    private static string Describe(string commandText, CommandType commandType)
    {
        if (commandType == CommandType.StoredProcedure)
        {
            return commandText;
        }

        var collapsed = string.Join(' ', commandText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= 40 ? collapsed : $"{collapsed[..37]}...";
    }
}
