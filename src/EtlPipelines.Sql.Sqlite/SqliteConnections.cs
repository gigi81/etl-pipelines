using EtlPipelines.Sql.Connections;
using EtlPipelines.Sql.Loading;
using EtlPipelines.Sql.Ports;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace EtlPipelines.Sql.Sqlite;

/// <summary>Opens SQLite connections for a pipeline.</summary>
/// <remarks>
/// There is no bulk-load fast path here and no <see cref="IBulkLoader"/> to register, because SQLite
/// has no bulk API: the fastest way in is a prepared INSERT reused inside one transaction, which is
/// exactly what <see cref="SqlSink{TRow}"/> does on its own. The transaction is what matters — without
/// one SQLite commits per statement and a large load crawls.
/// </remarks>
public static class SqliteConnections
{
    /// <summary>A factory that opens a new connection per run.</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    public static Func<CancellationToken, ValueTask<DbConnection>> Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return async cancellationToken =>
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        };
    }

    /// <summary>
    /// Registers a named SQLite connection, taking its connection string from configuration.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="name">
    /// The name <c>FromSql</c> and <c>ToSqlTable</c> refer to, and the name of the connection string
    /// under <c>ConnectionStrings</c>.
    /// </param>
    /// <remarks>
    /// No bulk loader is registered with it, for the reason given above: the sink's own prepared
    /// INSERT inside one transaction already is the fast path on SQLite.
    /// </remarks>
    /// <param name="configure">Statements to run on each connection once it is open.</param>
    public static IServiceCollection AddSqliteConnection(
        this IServiceCollection services,
        string name,
        Action<DbConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddDbConnection(name, OpenAsync, Options(configure));
    }

    /// <summary>
    /// Registers a named SQLite connection with the connection string given here rather than read
    /// from configuration.
    /// </summary>
    /// <remarks>
    /// For a database whose path is only known at run time — a file in a directory the run was
    /// handed, which is what the samples have.
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="name">The name the pipeline refers to this connection by.</param>
    /// <param name="connectionString">The connection string to use, in full.</param>
    /// <param name="configure">Statements to run on each connection once it is open.</param>
    public static IServiceCollection AddSqliteConnection(
        this IServiceCollection services,
        string name,
        string connectionString,
        Action<DbConnectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddDbConnection(name, connectionString, OpenAsync, Options(configure));
    }

    private static async ValueTask<DbConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// This engine has nothing worth naming beyond the statements themselves — see
    /// <see cref="DbConnectionOptions.SessionStatements"/>.
    /// </summary>
    private static DbConnectionOptions Options(Action<DbConnectionOptions>? configure)
    {
        var options = new DbConnectionOptions();
        configure?.Invoke(options);

        return options;
    }
}
