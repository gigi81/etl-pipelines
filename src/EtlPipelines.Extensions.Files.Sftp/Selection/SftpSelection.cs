using EtlPipelines.Extensions.Files.Configuration;

namespace EtlPipelines.Extensions.Files.Sftp.Selection;

/// <summary>
/// Which remote files a download selects: every file matching <see cref="Pattern"/> in
/// <see cref="Directory"/>.
/// </summary>
/// <remarks>
/// A remote path is not an <see cref="System.IO.Abstractions.IFileInfo"/>, so this does not implement
/// <c>EtlPipelines.Extensions.Files.Selection.IFileSelection</c> - it shares only the part that must agree with
/// the local selection types, the glob matcher in <c>EtlPipelines.Extensions.Files.Selection.FilePatterns</c>.
/// </remarks>
public sealed record SftpSelection(string Directory, string Pattern = "*")
{
    /// <summary>Whether the pattern also matches files in subdirectories. Defaults to <see langword="false"/>.</summary>
    public bool Recursive { get; init; }

    /// <summary>What the matched files are sorted by before the stage acts on them. Defaults to <see cref="FileOrder.Name"/>.</summary>
    public FileOrder Order { get; init; } = FileOrder.Name;

    /// <summary>Whether the sort in <see cref="Order"/> runs highest-to-lowest. Defaults to <see langword="false"/>.</summary>
    public bool Descending { get; init; }
}
