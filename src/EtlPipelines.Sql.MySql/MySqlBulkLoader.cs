using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MySqlConnector;

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
}
