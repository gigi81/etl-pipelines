namespace EtlPipelines.Extensions.Sql.Statements;

/// <summary>
/// Produces the statement that empties a table, for whichever engine one connection actually talks to.
/// </summary>
/// <remarks>
/// <para>
/// Real <c>TRUNCATE TABLE</c> is not one statement everywhere — SQLite has none at all — so
/// <see cref="TruncateTableStage"/> resolves this from the container under the connection's name,
/// exactly as <see cref="ISqlScriptParser"/> resolves its batching. A provider package that needs
/// something other than the ANSI default registers its own, keyed the same way; one that does not is
/// handed <see cref="TruncateTableStatement"/>.
/// </para>
/// <para>
/// Resolved at execute time rather than when the pipeline is composed, for the same reason a
/// connection string is: the container may not be finished being configured yet when
/// <c>builder.TruncateTable(...)</c> runs.
/// </para>
/// </remarks>
public interface ITruncateStatement
{
    /// <summary>The statement that empties <paramref name="table"/> on this engine.</summary>
    string For(string table);
}
