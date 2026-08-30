using System.Runtime.CompilerServices;

namespace EtlPipelines.Extensions.Sql.Scripts;

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
    public IAsyncEnumerable<string> ParseAsync(TextReader script, CancellationToken cancellationToken)
    {
        // An iterator method never runs its body - not even a leading argument check - until the
        // caller starts enumerating it, so the guard has to live in a non-iterator wrapper to fire
        // when the caller thinks it does.
        ArgumentNullException.ThrowIfNull(script);

        return ParseCoreAsync(script, cancellationToken);
    }

    private static async IAsyncEnumerable<string> ParseCoreAsync(
        TextReader script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var text = await script.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return text;
        }
    }
}
