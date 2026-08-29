namespace EtlPipelines.Extensions.Sql.Statements;

/// <summary>
/// Real <c>TRUNCATE TABLE</c> — what every engine this library supports runs, except SQLite.
/// </summary>
/// <remarks>
/// The fallback when a connection was registered without an <see cref="ITruncateStatement"/> of its
/// own, which today is every provider but SQLite: SQL Server, PostgreSQL, MySQL and Oracle all accept
/// this literally, and it is worth reaching for over a row-by-row <c>DELETE</c> on every one of them —
/// no per-row logging, and it resets an identity column besides.
/// </remarks>
public sealed class TruncateTableStatement : ITruncateStatement
{
    /// <summary>The shared instance. The statement holds no state.</summary>
    public static TruncateTableStatement Instance { get; } = new();

    /// <inheritdoc />
    public string For(string table) => $"TRUNCATE TABLE {table}";
}
