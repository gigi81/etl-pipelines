using System.IO.Abstractions;

namespace EtlPipelines.Sql.Scripts;

/// <summary>
/// Splits a script file into the batches a server will accept one at a time.
/// </summary>
/// <remarks>
/// A file is not a statement. Engines disagree about what separates one statement from the next, and
/// the disagreements are not cosmetic: SQL Server ends a batch on a line reading <c>GO</c>, which is
/// not SQL at all and which the server has never heard of; MySQL lets a script change its own
/// delimiter so a procedure body can contain semicolons; Oracle needs to know whether the file
/// terminates statements with <c>;</c> or <c>/</c>, and must not have the semicolon stripped off the
/// <c>END;</c> of a program unit. PostgreSQL and SQLite want none of it and take the file whole.
/// <para>
/// Each provider package registers its own under the connection name, so naming a connection is all
/// it takes to get the right one.
/// </para>
/// </remarks>
public interface ISqlScriptParser
{
    /// <summary>Reads <paramref name="script"/> and yields each batch in order.</summary>
    IAsyncEnumerable<string> ParseAsync(IFileInfo script, CancellationToken cancellationToken);
}
