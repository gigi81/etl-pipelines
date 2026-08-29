using System.IO.Enumeration;

namespace EtlPipelines.Extensions.Files.Selection;

/// <summary>
/// The glob matcher <see cref="PatternFileSelection"/> uses, exposed so the Sftp satellite package
/// can match a remote directory listing the same way a local one is matched.
/// </summary>
/// <remarks>
/// Scope is deliberately just <c>*</c> and <c>?</c>, plus recursion as a separate flag - what
/// <see cref="FileSystemName.MatchesSimpleExpression"/> gives for free from the shared framework.
/// There is no <c>**</c> and no brace-set support; that needs
/// <c>Microsoft.Extensions.FileSystemGlobbing</c>, a dependency this package does not take.
/// </remarks>
public static class FilePatterns
{
    /// <summary>Whether <paramref name="fileName"/> matches a pattern of <c>*</c> and <c>?</c>.</summary>
    public static bool Matches(string fileName, string pattern, bool ignoreCase) =>
        FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase);
}
