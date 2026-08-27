using EtlPipelines.Sql.Statements;

namespace EtlPipelines.Sql.Sqlite;

/// <summary>Empties a table the way SQLite actually can: there is no <c>TRUNCATE</c> statement at all.</summary>
public sealed class SqliteTruncateStatement : ITruncateStatement
{
    /// <summary>The shared instance. The statement holds no state.</summary>
    public static SqliteTruncateStatement Instance { get; } = new();

    /// <inheritdoc />
    public string For(string table) => $"DELETE FROM {table}";
}
