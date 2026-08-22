using EtlPipelines.Sql.Scripts;
using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text;

namespace EtlPipelines.Sql.MySql;

/// <summary>
/// Splits a script on its current delimiter, which the script itself may change.
/// </summary>
/// <remarks>
/// A procedure body is full of semicolons, so a script that defines one says <c>DELIMITER $$</c>
/// first and the client is expected to honour it. That is a client convention, like SQL Server's
/// <c>GO</c>: the server never sees the line.
/// </remarks>
public sealed class MySqlScriptParser : ISqlScriptParser
{
    /// <inheritdoc />
    public async IAsyncEnumerable<string> ParseAsync(
        IFileInfo script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);

        var delimiter = ";";
        var buffer = new StringBuilder();

        await using var file = script.OpenRead();
        using var reader = new StreamReader(file);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        while (line is not null)
        {
            if (!TakeDelimiter(line, ref delimiter))
            {
                if (line.Trim().EndsWith(delimiter, StringComparison.Ordinal))
                {
                    Append(buffer, RemoveDelimiter(line, delimiter));

                    if (HasSql(buffer))
                    {
                        yield return buffer.ToString();
                    }

                    buffer.Clear();
                }
                else
                {
                    Append(buffer, line);
                }
            }

            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        if (HasSql(buffer))
        {
            yield return buffer.ToString();
        }
    }

    /// <summary>
    /// Whether the buffer holds anything the server would accept.
    /// </summary>
    /// <remarks>
    /// A comment line does not end with the delimiter, so it stays in the buffer and comes out
    /// attached to the statement that follows it - except at the end of the file, where there is no
    /// such statement and the buffer would be flushed as a batch of nothing but comments. MySQL
    /// answers that with <c>ER_EMPTY_QUERY</c>, so it never leaves the parser.
    /// </remarks>
    private static bool HasSql(StringBuilder buffer)
    {
        if (buffer.Length == 0)
        {
            return false;
        }

        foreach (var line in buffer.ToString().Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !IsComment(trimmed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The two comment forms that run to the end of the line. <c>--</c> only opens a comment when
    /// followed by whitespace, which is why the bare <c>--</c> operator is not one.
    /// </summary>
    private static bool IsComment(string trimmedLine)
    {
        if (trimmedLine.StartsWith('#'))
        {
            return true;
        }

        return trimmedLine.StartsWith("--", StringComparison.Ordinal)
            && (trimmedLine.Length == 2 || char.IsWhiteSpace(trimmedLine[2]));
    }

    private static void Append(StringBuilder buffer, string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            buffer.AppendLine(line);
        }
    }

    private static string RemoveDelimiter(string line, string delimiter)
    {
        line = line.Trim();
        return line[..^delimiter.Length];
    }

    private static bool TakeDelimiter(string line, ref string delimiter)
    {
        line = line.Trim();

        if (!line.StartsWith("DELIMITER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        delimiter = line.Replace("DELIMITER", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return true;
    }
}
