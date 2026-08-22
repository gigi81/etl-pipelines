using EtlPipelines.Sql.Loading;
using EtlPipelines.Sql.Ports;
using EtlPipelines.Sql.Scripts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace EtlPipelines.Sql.SqlServer;

/// <summary>Loads batches through <see cref="SqlBulkCopy"/>.</summary>
/// <remarks>
/// Columns are mapped by name rather than by position: the batch reader presents the row type's
/// properties in their own order, which is not the order the table declares them in.
/// </remarks>
public sealed class SqlServerBulkLoader : IBulkLoader
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

        using var copy = new SqlBulkCopy(
            (SqlConnection)connection,
            SqlBulkCopyOptions.Default,
            (SqlTransaction?)transaction)
        {
            DestinationTableName = table,
        };

        for (var i = 0; i < columns.Count; i++)
        {
            copy.ColumnMappings.Add(columns[i], columns[i]);
        }

        await copy.WriteToServerAsync(rows, cancellationToken).ConfigureAwait(false);
        return copy.RowsCopied;
    }
}

/// <summary>Registers the SQL Server bulk-load path.</summary>
public static class SqlServerExtensions
{
    /// <summary>Makes <see cref="SqlSink{TRow}"/> use <see cref="SqlBulkCopy"/> instead of INSERTs.</summary>
    public static IServiceCollection AddSqlServerBulkLoader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBulkLoader, SqlServerBulkLoader>();
        return services;
    }

    /// <summary>A factory that opens a new connection per run.</summary>
    public static Func<CancellationToken, ValueTask<DbConnection>> Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return async cancellationToken =>
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        };
    }

    /// <summary>
    /// Registers a named SQL Server connection, taking its connection string from configuration, and
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
    public static IServiceCollection AddSqlServerConnection(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, OpenAsync);
        services.AddKeyedSingleton<IBulkLoader, SqlServerBulkLoader>(name);
        services.AddKeyedSingleton<ISqlScriptParser, SqlServerScriptParser>(name);

        return services;
    }

    /// <summary>
    /// Registers a named SQL Server connection with the connection string given here rather than read
    /// from configuration, and that engine's bulk-load fast path alongside it.
    /// </summary>
    /// <remarks>
    /// For a database whose address is only known at run time — a throwaway container in a test.
    /// </remarks>
    public static IServiceCollection AddSqlServerConnection(
        this IServiceCollection services,
        string name,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, connectionString, OpenAsync);
        services.AddKeyedSingleton<IBulkLoader, SqlServerBulkLoader>(name);
        services.AddKeyedSingleton<ISqlScriptParser, SqlServerScriptParser>(name);

        return services;
    }

    private static async ValueTask<DbConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
