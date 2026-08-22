using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

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
}
