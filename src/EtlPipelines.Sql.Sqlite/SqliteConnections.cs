using System.Data.Common;
using Microsoft.Data.Sqlite;

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
}
