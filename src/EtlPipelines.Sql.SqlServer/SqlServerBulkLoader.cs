using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
}
