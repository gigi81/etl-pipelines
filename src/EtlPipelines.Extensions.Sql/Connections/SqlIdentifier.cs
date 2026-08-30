namespace EtlPipelines.Extensions.Sql.Connections;

/// <summary>Checks that a name is safe to put into a statement.</summary>
/// <remarks>
/// Session settings take no bind variables — <c>ALTER SESSION SET CURRENT_SCHEMA = :schema</c> is not
/// a thing — so a schema name has to be written into the SQL. It will often have come from
/// configuration, which is exactly the kind of place a value nobody vetted arrives from, so it is
/// checked rather than trusted.
/// </remarks>
public static class SqlIdentifier
{
    /// <summary>Returns <paramref name="value"/> when it is a plain unquoted identifier.</summary>
    /// <param name="value">The name to check.</param>
    /// <param name="parameterName">What to call it in the exception.</param>
    /// <exception cref="ArgumentException">
    /// It is empty, does not start with a letter, or holds anything but letters, digits, <c>_</c>,
    /// <c>$</c> or <c>#</c> — the characters every engine here accepts without quoting.
    /// </exception>
    public static string Require(string value, string parameterName)
    {
        // S3236 wants this argument dropped so CallerArgumentExpression fills it in - but that would
        // report "value", this method's own parameter, instead of parameterName: the name Require's
        // caller actually passed in, which is the whole point of taking it as a parameter here.
#pragma warning disable S3236
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
#pragma warning restore S3236

        if (!char.IsLetter(value[0]))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid identifier: it has to start with a letter.", parameterName);
        }

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '$' or '#')
            {
                continue;
            }

            throw new ArgumentException(
                $"'{value}' is not a valid identifier: '{character}' is not allowed. It goes into a " +
                "statement unquoted, so it may hold only letters, digits, underscore, dollar or hash.",
                parameterName);
        }

        return value;
    }
}
