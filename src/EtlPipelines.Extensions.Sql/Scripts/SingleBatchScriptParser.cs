using System.Runtime.CompilerServices;

namespace EtlPipelines.Sql.Scripts;

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
        TextReader script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);

        var text = await script.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return text;
        }
    }
}
