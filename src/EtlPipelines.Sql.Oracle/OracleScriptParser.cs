using EtlPipelines.Sql.Scripts;
using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text;

namespace EtlPipelines.Sql.Oracle;

/// <summary>
/// Splits a script the way SQL*Plus does, on whichever terminator the file uses.
/// </summary>
/// <remarks>
/// Oracle is the awkward one. A file terminates statements with <c>;</c> or with <c>/</c> on its own
/// line, and which it is has to be worked out from the file. The trailing semicolon then has to be
/// stripped, because the driver will not take it — except on the <c>END;</c> of a PL/SQL unit, where
/// it belongs to the statement and removing it makes the server reject the whole body.
/// </remarks>
public sealed partial class OracleScriptParser : ISqlScriptParser
{
    private static readonly char[] Whitespace = ['\t', '\n', '\r', ' '];

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ParseAsync(
        IFileInfo script,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);

        var lines = await ReadLinesAsync(script, cancellationToken).ConfigureAwait(false);
        var terminator = DetectTerminator(lines);
        var buffer = new StringBuilder();
        var quotes = 0;

        foreach (var line in lines)
        {
            var trim = line.Trim();

            if (buffer.Length > 0 || !CanIgnore(trim))
            {
                buffer.AppendLine(line);
            }

            quotes += trim.Count(c => c == '\'');

            // A line holding nothing but the terminator always closes the statement, the way
            // SQL*Plus does. Quote counting cannot be trusted on its own: a single apostrophe in a
            // PL/SQL comment leaves it convinced the statement is still inside a string literal,
            // and every following statement gets swallowed into the same command.
            var terminatorLine = trim.Length == 1 && trim[0] == terminator;

            if (terminatorLine || (quotes % 2 == 0 && trim.EndsWith(terminator)))
            {
                var command = Clean(buffer.ToString(), terminator);

                if (!string.IsNullOrWhiteSpace(command))
                {
                    yield return command;
                }

                buffer.Clear();
                quotes = 0;
            }
        }

        // Cleaned like every other batch. Leaving the last one raw is the one place the original of
        // this parser did not, and it shows in a script that closes its PL/SQL with / and its plain
        // DDL with ; - the trailing CREATE TABLE keeps a semicolon Oracle answers with ORA-00911.
        var last = Clean(buffer.ToString(), terminator);

        if (!string.IsNullOrWhiteSpace(last))
        {
            yield return last;
        }
    }

    /// <summary>Matches the END of a PL/SQL unit, whose semicolon belongs to the statement.</summary>
    [GeneratedRegex(@"\bEND\s*(""?[A-Za-z0-9_$#]+""?)?\s*;$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlSqlEnd();

    private static async Task<string[]> ReadLinesAsync(IFileInfo script, CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        await using var file = script.OpenRead();
        using var reader = new StreamReader(file);

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        while (line is not null)
        {
            lines.Add(line);
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        return [.. lines];
    }

    private static char DetectTerminator(IEnumerable<string> lines) =>
        lines.Any(line => line.Trim().StartsWith('/')) ? '/' : ';';

    private static string Clean(string input, char terminator)
    {
        var sql = input.Trim(Whitespace).TrimEnd(terminator).Trim(Whitespace);

        // Stripping the trailing semicolon off a program unit turns its END; into an END and the
        // server rejects the whole body.
        return PlSqlEnd().IsMatch(sql) ? sql : sql.TrimEnd(';').Trim(Whitespace);
    }

    private static bool CanIgnore(string trim) =>
        string.IsNullOrEmpty(trim)
        || trim.StartsWith("rem", StringComparison.OrdinalIgnoreCase)
        || trim.StartsWith("set", StringComparison.OrdinalIgnoreCase)
        || trim.StartsWith("prompt", StringComparison.OrdinalIgnoreCase);
}
