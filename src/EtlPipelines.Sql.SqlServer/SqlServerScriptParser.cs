using EtlPipelines.Sql.Scripts;
using System.Runtime.CompilerServices;
using System.Text;

namespace EtlPipelines.Sql.SqlServer;

/// <summary>
/// Splits a script into batches the way sqlcmd does: a line holding nothing but <c>GO</c> ends the
/// batch.
/// </summary>
/// <remarks>
/// <c>GO</c> is not SQL and the server has never heard of it — it is a convention of the client
/// tools, which is why sending a file containing one straight to <c>ExecuteNonQuery</c> fails.
/// </remarks>
public sealed class SqlServerScriptParser : ISqlScriptParser
{
    /// <inheritdoc />
    public async IAsyncEnumerable<string> ParseAsync(
        TextReader script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);

        var buffer = new StringBuilder();
        var line = await script.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        while (line is not null)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (HasContent(buffer))
                {
                    yield return buffer.ToString();
                }

                buffer.Clear();
            }
            else
            {
                buffer.AppendLine(line);
            }

            line = await script.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        if (HasContent(buffer))
        {
            yield return buffer.ToString();
        }
    }

    /// <summary>
    /// An empty batch - two terminators in a row, or a trailing one at the end of the file - is not
    /// something the server will accept, so it never leaves the parser.
    /// </summary>
    private static bool HasContent(StringBuilder buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
        {
            if (!char.IsWhiteSpace(buffer[i]))
            {
                return true;
            }
        }

        return false;
    }
}
