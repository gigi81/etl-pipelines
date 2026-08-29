using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using System.Diagnostics;

namespace EtlPipelines.Sql.Stages;

/// <summary>
/// Empties a table as a stage of a pipeline, using whichever statement the connection's engine
/// actually runs.
/// </summary>
/// <remarks>
/// <para>
/// The statement is resolved from the <see cref="ITruncateStatement"/> registered under the
/// connection's name, exactly as <see cref="SqlScriptStage"/> resolves its <see cref="ISqlScriptParser"/>
/// — at execute time, not when the pipeline is composed, and falling back to real
/// <see cref="TruncateTableStatement"/> when a provider has nothing to say about it, which is what
/// every engine but SQLite actually runs.
/// </para>
/// <para>
/// A separate stage from <see cref="SqlCommandStage"/> rather than a call into it, because the text
/// to run is not known until a connection has been named and its dialect looked up — <c>RunSql</c>
/// fixes its statement at composition time, before any of that is possible.
/// </para>
/// </remarks>
public sealed class TruncateTableStage : IPipelineStage
{
    private readonly string _connectionName;
    private readonly string _table;
    private readonly SqlCommandOptions _options;

    /// <summary>Empties <paramref name="table"/> over the connection registered as <paramref name="connectionName"/>.</summary>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="table">The table to empty.</param>
    /// <param name="options">Command timeout and what the stage is called.</param>
    public TruncateTableStage(string connectionName, string table, SqlCommandOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        _connectionName = connectionName;
        _table = table;
        _options = options ?? new SqlCommandOptions();

        Name = options?.Name ?? $"truncate {table}";
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

        var statement = context.Services.GetKeyedService<ITruncateStatement>(_connectionName)
            ?? TruncateTableStatement.Instance;

        var commandText = statement.For(_table);

        try
        {
            await using var connection = await context.Services
                .GetRequiredDbConnectionFactory(_connectionName)
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = commandText;

            if (_options.CommandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            _options.Configure?.Invoke(command);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            return Error.Failure($"sql.truncate.{Name}.failed", $"{commandText}: {exception.Message}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
