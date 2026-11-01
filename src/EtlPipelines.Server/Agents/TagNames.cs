using System.Text.RegularExpressions;

namespace EtlPipelines.Server.Agents;

/// <summary>What a tag may look like, and how tags are compared.</summary>
/// <remarks>
/// Tags are short labels such as <c>linux</c>, <c>gpu</c> or <c>env:prod</c>. They are lower case, so
/// <c>GPU</c> and <c>gpu</c> are one tag: that is what makes matching an agent to a pipeline
/// predictable. The ones a user types are normalized here; the ones an agent reports about itself are
/// only ever compared with <see cref="Comparer"/>.
/// </remarks>
public static partial class TagNames
{
    /// <summary>The longest tag.</summary>
    public const int MaxLength = 50;

    /// <summary>The most tags one agent (or one pipeline) can be given.</summary>
    public const int MaxCount = 20;

    /// <summary>How tags are compared.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Trims, lower-cases and de-duplicates <paramref name="tags"/>. False, with the reason, when one is empty
    /// of letters and digits, too long or uses a character other than <c>a-z 0-9 . _ : / -</c>, or there are too many.
    /// </summary>
    public static bool TryNormalize(IEnumerable<string>? tags, out IReadOnlyList<string> normalized, out string? error)
    {
        var result = new List<string>();
        error = null;

        foreach (var raw in tags ?? [])
        {
            var tag = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            if (tag.Length == 0)
            {
                continue;
            }

            if (tag.Length > MaxLength)
            {
                error = $"A tag can be at most {MaxLength} characters ('{tag[..20]}...' is longer).";
                break;
            }

            if (!Allowed().IsMatch(tag))
            {
                error = $"'{tag}' is not a valid tag: use letters, digits and . _ : / - (starting with a letter or digit).";
                break;
            }

            if (!result.Contains(tag))
            {
                result.Add(tag);
            }
        }

        if (error is null && result.Count > MaxCount)
        {
            error = $"At most {MaxCount} tags are allowed.";
        }

        normalized = error is null ? result : [];
        return error is null;
    }

    /// <summary>Both lists as one, without repeats.</summary>
    public static IReadOnlyList<string> Union(IEnumerable<string> first, IEnumerable<string> second) =>
        [.. first.Concat(second).Distinct(Comparer)];

    [GeneratedRegex("^[a-z0-9][a-z0-9._:/-]*$")]
    private static partial Regex Allowed();
}
