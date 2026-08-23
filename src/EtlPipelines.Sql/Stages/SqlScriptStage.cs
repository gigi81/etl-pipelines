using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;
using System.Diagnostics;

namespace EtlPipelines.Sql.Stages;

/// <summary>
/// Runs a SQL script file as a stage of a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The script is split into batches by the <see cref="ISqlScriptParser"/> registered under the
/// connection's name, and each batch is executed in order over one connection. That split is the
/// whole difficulty — see <see cref="ISqlScriptParser"/> for why a script is not a statement — and
/// it is why the parser comes from the connection rather than being chosen here.
/// </para>
/// <para>
/// Where the text came from is <see cref="ISqlScriptSource"/>'s business: a file beside the
/// application, or a resource compiled into it.
/// </para>
/// <para>
/// The batch that failed is named in the error, since a script that dies on its fortieth statement
/// is otherwise a stack trace with no address.
/// </para>
/// </remarks>
public sealed class SqlScriptStage : IPipelineStage
{
    private readonly string _connectionName;
    private readonly ISqlScriptSource _script;
    private readonly SqlScriptOptions _options;

    /// <summary>Runs <paramref name="script"/> over the connection registered as <paramref name="connectionName"/>.</summary>
    public SqlScriptStage(string connectionName, ISqlScriptSource script, SqlScriptOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(script);

        _connectionName = connectionName;
        _script = script;
        _options = options ?? new SqlScriptOptions();

        Name = options?.Name ?? script.Name;
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

        var opened = await _script.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (opened.IsError)
        {
            return opened.Errors;
        }

        using var text = opened.Value;

        var parser = context.Services.GetKeyedService<ISqlScriptParser>(_connectionName)
            ?? SingleBatchScriptParser.Instance;

        var batch = 0;

        try
        {
            await using var connection = await context.Services
                .GetRequiredDbConnectionFactory(_connectionName)
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            await foreach (var sql in parser.ParseAsync(text, cancellationToken).ConfigureAwait(false))
            {
                batch++;

                await using var command = connection.CreateCommand();
                command.CommandText = sql;

                if (_options.CommandTimeout is { } timeout)
                {
                    command.CommandTimeout = timeout;
                }

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (DbException exception)
        {
            return Error.Failure(
                $"sql.script.{Name}.failed",
                $"{Name}, batch {batch}: {exception.Message}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
