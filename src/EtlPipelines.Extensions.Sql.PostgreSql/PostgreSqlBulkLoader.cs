using EtlPipelines.Sql.Connections;
using EtlPipelines.Sql.Loading;
using EtlPipelines.Sql.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data.Common;

namespace EtlPipelines.Sql.PostgreSql;

/// <summary>Loads batches through PostgreSQL's binary <c>COPY</c>.</summary>
/// <remarks>
/// Binary COPY is by a wide margin the fastest way into PostgreSQL, and it runs inside whatever
/// transaction the connection is already in, so the sink's commit-on-success behaviour still holds.
/// Values are written untyped — Npgsql infers the parameter type from the CLR value — which keeps
/// this independent of the destination table's column types. Column names go in exactly as the row
/// type spells them, unquoted, so PostgreSQL folds them to lower case as it would for any other
/// statement; a table whose columns were created quoted and mixed-case will not match.
/// </remarks>
public sealed class PostgreSqlBulkLoader : IBulkLoader
{
    /// <inheritdoc />
    public async ValueTask<int> LoadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns,
        DbDataReader rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);

        var columnList = string.Join(", ", columns);
        var copy = $"COPY {table} ({columnList}) FROM STDIN (FORMAT BINARY)";

        await using var writer = await ((NpgsqlConnection)connection)
            .BeginBinaryImportAsync(copy, cancellationToken)
            .ConfigureAwait(false);

        var written = 0;
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < columns.Count; i++)
            {
                var value = rows.GetValue(i);
                if (value is DBNull)
                {
                    await writer.WriteNullAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
                }
            }

            written++;
        }

        // Nothing is durable until the import is completed; abandoning the writer discards the batch.
        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }
}

/// <summary>Registers the PostgreSQL bulk-load path.</summary>
public static class PostgreSqlExtensions
{
    /// <summary>Makes <see cref="SqlSink{TRow}"/> use binary COPY instead of INSERTs.</summary>
    public static IServiceCollection AddPostgreSqlBulkLoader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBulkLoader, PostgreSqlBulkLoader>();
        return services;
    }

    /// <summary>A factory that opens a new connection per run.</summary>
    public static Func<CancellationToken, ValueTask<DbConnection>> Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return async cancellationToken =>
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        };
    }

    /// <summary>
    /// Registers a named PostgreSQL connection, taking its connection string from configuration, and
    /// that engine's bulk-load fast path alongside it.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="name">
    /// The name <c>FromSql</c> and <c>ToSqlTable</c> refer to, and the name of the connection string
    /// under <c>ConnectionStrings</c>.
    /// </param>
    /// <remarks>
    /// The loader is keyed to the connection name rather than registered once for the container, so a
    /// pipeline reading from one engine and writing to another gets the right fast path at each end.
    /// </remarks>
    /// <param name="configure">The schema to resolve names against, and any other session statements.</param>
    public static IServiceCollection AddPostgreSqlConnection(
        this IServiceCollection services,
        string name,
        Action<PostgreSqlConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, OpenAsync, Options(configure));
        services.AddKeyedSingleton<IBulkLoader, PostgreSqlBulkLoader>(name);

        return services;
    }

    /// <summary>
    /// Registers a named PostgreSQL connection with the connection string given here rather than read
    /// from configuration, and that engine's bulk-load fast path alongside it.
    /// </summary>
    /// <remarks>
    /// For a database whose address is only known at run time — a throwaway container in a test.
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="name">The name the pipeline refers to this connection by.</param>
    /// <param name="connectionString">The connection string to use, in full.</param>
    /// <param name="configure">The schema to resolve names against, and any other session statements.</param>
    public static IServiceCollection AddPostgreSqlConnection(
        this IServiceCollection services,
        string name,
        string connectionString,
        Action<PostgreSqlConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, connectionString, OpenAsync, Options(configure));
        services.AddKeyedSingleton<IBulkLoader, PostgreSqlBulkLoader>(name);

        return services;
    }

    private static async ValueTask<DbConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Turns the named settings into the statements that apply them.</summary>
    private static PostgreSqlConnectionOptions Options(Action<PostgreSqlConnectionOptions>? configure)
    {
        var options = new PostgreSqlConnectionOptions();
        configure?.Invoke(options);

        if (options.CurrentSchema is { } schema)
        {
            options.SessionStatements.Add(
                $"SET search_path TO {SqlIdentifier.Require(schema, nameof(options.CurrentSchema))}");
        }

        return options;
    }
}
