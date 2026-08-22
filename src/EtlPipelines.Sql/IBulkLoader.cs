using System.Data.Common;

namespace EtlPipelines.Sql;

/// <summary>
/// A provider's fast path for getting many rows into a table.
/// </summary>
/// <remarks>
/// <para>
/// Every database worth loading into has something faster than a row of INSERTs — SqlBulkCopy,
/// PostgreSQL's binary COPY, MySQL's LOAD DATA, Oracle's array binding — and each lives in its own
/// driver package. This is the seam between them and <see cref="SqlSink{TRow}"/>, so the sink can use
/// whichever one is registered without referencing any driver itself.
/// </para>
/// <para>
/// Rows arrive as a <see cref="DbDataReader"/> because that is the currency the bulk APIs already
/// speak: SqlBulkCopy and MySqlBulkCopy both take one directly, so those implementations are a
/// handful of lines.
/// </para>
/// </remarks>
public interface IBulkLoader
{
    /// <summary>Writes every row the reader yields into <paramref name="table"/>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction to enlist in, when the sink opened one.</param>
    /// <param name="table">The destination table.</param>
    /// <param name="columns">The destination columns, in the order the reader presents them.</param>
    /// <param name="rows">The batch, as a forward-only reader.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The number of rows written.</returns>
    ValueTask<int> LoadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns,
        DbDataReader rows,
        CancellationToken cancellationToken);
}
