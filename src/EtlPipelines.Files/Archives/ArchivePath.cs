using System.IO.Abstractions;

namespace EtlPipelines.Files.Archives;

/// <summary>
/// Resolves one archive entry's name against the directory it is being extracted into, refusing any
/// entry that would land outside it - the "Zip Slip" vulnerability, present in every archive format
/// this package reads because none of them constrain what an entry may call itself.
/// </summary>
/// <remarks>
/// Called for every entry of every format, so there is exactly one implementation of this check and
/// exactly one place to test it. Link and device entries are the other half of the same attack - a
/// symbolic link aimed outside the target, followed by a regular entry writing "through" it by name -
/// and are rejected by the extraction stage itself, which is the layer that knows an entry's type.
/// </remarks>
internal static class ArchivePath
{
    /// <summary>
    /// Resolves <paramref name="entryName"/> against <paramref name="targetRoot"/>, or an error if
    /// the entry would resolve outside it.
    /// </summary>
    public static ErrorOr<string> Resolve(IFileSystem fileSystem, string targetRoot, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return Unsafe(entryName, targetRoot, "the entry name is empty");
        }

        // Checked textually and regardless of the host OS, because the archive itself is host-
        // agnostic: one built on Windows can carry "C:\payload" or "\\server\share\x", and
        // Path.IsPathRooted("C:\\payload") is false when this process runs on Linux.
        if (fileSystem.Path.IsPathRooted(entryName)
            || entryName.StartsWith('/') || entryName.StartsWith('\\')
            || (entryName.Length >= 2 && entryName[1] == ':')
            || entryName.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return Unsafe(entryName, targetRoot, "the entry name is rooted");
        }

        var normalized = entryName.Replace('\\', fileSystem.Path.DirectorySeparatorChar)
                                   .Replace('/', fileSystem.Path.DirectorySeparatorChar);

        var root = fileSystem.Path.GetFullPath(targetRoot);
        var rootWithSeparator = root.EndsWith(fileSystem.Path.DirectorySeparatorChar)
            ? root
            : root + fileSystem.Path.DirectorySeparatorChar;

        // GetFullPath is what collapses ".." segments - this is the step that actually answers
        // "where would this entry land".
        var resolved = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(root, normalized));

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!resolved.StartsWith(rootWithSeparator, comparison) && !string.Equals(resolved, root, comparison))
        {
            return Unsafe(entryName, targetRoot, "it resolves outside the extraction directory");
        }

        return resolved;
    }

    private static ErrorOr<string> Unsafe(string entryName, string targetRoot, string reason) =>
        Error.Failure(
            "files.extract.unsafe_entry",
            $"Entry '{entryName}' cannot be extracted into '{targetRoot}': {reason}.");
}
