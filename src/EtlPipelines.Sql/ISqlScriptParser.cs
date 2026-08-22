using System.IO.Abstractions;
using System.Runtime.CompilerServices;

namespace EtlPipelines.Sql;

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

/// <summary>
/// Hands the file over whole, as one batch.
/// </summary>
/// <remarks>
/// What PostgreSQL and SQLite want: both accept several statements in one command, so splitting the
/// file would only invent failure modes. It is also the fallback when a connection was registered
/// without a parser of its own.
/// </remarks>
public sealed class SingleBatchScriptParser : ISqlScriptParser
{
    /// <summary>The shared instance. The parser holds no state.</summary>
    public static SingleBatchScriptParser Instance { get; } = new();

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ParseAsync(
        IFileInfo script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);

        using var reader = new StreamReader(script.OpenRead());
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return text;
        }
    }
}
