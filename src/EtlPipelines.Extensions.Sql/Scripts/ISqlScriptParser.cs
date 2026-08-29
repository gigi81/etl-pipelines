namespace EtlPipelines.Extensions.Sql.Scripts;

/// <summary>
/// Splits a script into the batches a server will accept one at a time.
/// </summary>
/// <remarks>
/// A script is not a statement. Engines disagree about what separates one statement from the next,
/// and the disagreements are not cosmetic: SQL Server ends a batch on a line reading <c>GO</c>, which
/// is not SQL at all and which the server has never heard of; MySQL lets a script change its own
/// delimiter so a procedure body can contain semicolons; Oracle needs to know whether the script
/// terminates statements with <c>;</c> or <c>/</c>, and must not have the semicolon stripped off the
/// <c>END;</c> of a program unit. PostgreSQL and SQLite want none of it and take the whole thing.
/// <para>
/// Each provider package registers its own under the connection name, so naming a connection is all
/// it takes to get the right one.
/// </para>
/// <para>
/// Text rather than a file: where the script came from is <see cref="ISqlScriptSource"/>'s business,
/// and splitting it works the same whether it was read off disk or out of an assembly.
/// </para>
/// </remarks>
public interface ISqlScriptParser
{
    /// <summary>Reads <paramref name="script"/> to the end and yields each batch in order.</summary>
    IAsyncEnumerable<string> ParseAsync(TextReader script, CancellationToken cancellationToken);
}
