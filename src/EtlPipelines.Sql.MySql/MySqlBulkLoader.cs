using EtlPipelines.Sql.Connections;
using EtlPipelines.Sql.Loading;
using EtlPipelines.Sql.Ports;
using EtlPipelines.Sql.Scripts;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using System.Data.Common;

namespace EtlPipelines.Sql.MySql;

/// <summary>Loads batches through <see cref="MySqlBulkCopy"/>. Serves MariaDB as well as MySQL.</summary>
/// <remarks>
/// <b>The connection string needs <c>AllowLoadLocalInfile=true</c></b>, and the server needs
/// <c>local_infile</c> enabled. MySqlBulkCopy is built on <c>LOAD DATA LOCAL INFILE</c>, which both
/// ends refuse by default, and the error when they do is about the statement rather than about the
/// setting — so it is worth setting deliberately rather than discovering.
/// </remarks>
public sealed class MySqlBulkLoader : IBulkLoader
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

        var copy = new MySqlBulkCopy((MySqlConnection)connection, (MySqlTransaction?)transaction)
        {
            DestinationTableName = table,
        };

        for (var i = 0; i < columns.Count; i++)
        {
            copy.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, columns[i]));
        }

        var result = await copy.WriteToServerAsync(rows, cancellationToken).ConfigureAwait(false);
        return (int)result.RowsInserted;
    }
}

/// <summary>Registers the MySQL bulk-load path.</summary>
public static class MySqlExtensions
{
    /// <summary>Makes <see cref="SqlSink{TRow}"/> use <see cref="MySqlBulkCopy"/> instead of INSERTs.</summary>
    public static IServiceCollection AddMySqlBulkLoader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBulkLoader, MySqlBulkLoader>();
        return services;
    }

    /// <summary>A factory that opens a new connection per run.</summary>
    public static Func<CancellationToken, ValueTask<DbConnection>> Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return async cancellationToken =>
        {
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        };
    }

    /// <summary>
    /// Registers a named MySQL and MariaDB connection, taking its connection string from configuration, and
    /// that engine's bulk-load fast path alongside it.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="name">
    /// The name <c>FromSql</c> and <c>ToSqlTable</c> refer to, and the name of the connection string
    /// under <c>ConnectionStrings</c>.
    /// </param>
    /// <remarks>
    /// The loader and the script parser are keyed to the connection name rather than registered once for
    /// the container, so a pipeline that touches two engines gets the right one at each end.
    /// </remarks>
    /// <param name="configure">The schema to resolve names against, and any other session statements.</param>
    public static IServiceCollection AddMySqlConnection(
        this IServiceCollection services,
        string name,
        Action<MySqlConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, OpenAsync, Options(configure));
        services.AddKeyedSingleton<IBulkLoader, MySqlBulkLoader>(name);
        services.AddKeyedSingleton<ISqlScriptParser, MySqlScriptParser>(name);

        return services;
    }

    /// <summary>
    /// Registers a named MySQL and MariaDB connection with the connection string given here rather than read
    /// from configuration, and that engine's bulk-load fast path alongside it.
    /// </summary>
    /// <remarks>
    /// For a database whose address is only known at run time — a throwaway container in a test.
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="name">The name the pipeline refers to this connection by.</param>
    /// <param name="connectionString">The connection string to use, in full.</param>
    /// <param name="configure">The schema to resolve names against, and any other session statements.</param>
    public static IServiceCollection AddMySqlConnection(
        this IServiceCollection services,
        string name,
        string connectionString,
        Action<MySqlConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, connectionString, OpenAsync, Options(configure));
        services.AddKeyedSingleton<IBulkLoader, MySqlBulkLoader>(name);
        services.AddKeyedSingleton<ISqlScriptParser, MySqlScriptParser>(name);

        return services;
    }

    private static async ValueTask<DbConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Turns the named settings into the statements that apply them.</summary>
    private static MySqlConnectionOptions Options(Action<MySqlConnectionOptions>? configure)
    {
        var options = new MySqlConnectionOptions();
        configure?.Invoke(options);

        if (options.CurrentSchema is { } schema)
        {
            options.SessionStatements.Add(
                $"USE {SqlIdentifier.Require(schema, nameof(options.CurrentSchema))}");
        }

        return options;
    }
}
