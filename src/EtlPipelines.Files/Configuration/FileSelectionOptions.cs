namespace EtlPipelines.Files.Configuration;

/// <summary>Settings shared by every file stage that selects many files from a directory by pattern.</summary>
public abstract class FileSelectionOptions : FileWriteOptions
{
    /// <summary>Whether the pattern also matches files in subdirectories. Defaults to <see langword="false"/>.</summary>
    public bool Recursive { get; set; }

    /// <summary>What the matched files are sorted by before the stage acts on them. Defaults to <see cref="FileOrder.Name"/>.</summary>
    public FileOrder Order { get; set; } = FileOrder.Name;

    /// <summary>Whether the sort in <see cref="Order"/> runs highest-to-lowest. Defaults to <see langword="false"/>.</summary>
    public bool Descending { get; set; }

    /// <summary>
    /// The fewest files a pattern may match before the stage fails. Defaults to 0: an inbox with
    /// nothing new tonight is not an error unless a job specifically expects it not to be empty.
    /// </summary>
    public int MinimumFiles { get; set; }

    /// <summary>
    /// Case sensitivity for the pattern. Defaults to <see cref="System.IO.MatchCasing.PlatformDefault"/>,
    /// matching how the BCL itself defaults <see cref="EnumerationOptions"/>.
    /// </summary>
    public MatchCasing MatchCasing { get; set; } = MatchCasing.PlatformDefault;

    /// <summary>
    /// Whether a directory entry the process cannot read is silently skipped rather than failing the
    /// whole selection. Defaults to <see langword="true"/>, matching <see cref="EnumerationOptions"/>'s
    /// own default.
    /// </summary>
    /// <remarks>
    /// Set to <see langword="false"/> for an inbox where a permission problem must be loud rather
    /// than silently shortening the file list - a real risk on a share with mixed ACLs, where the
    /// alternative is data quietly going missing downstream. At the time of writing the in-memory
    /// test filesystem this package's own tests use does not support that value; a directory holding
    /// files it genuinely cannot read is what actually exercises it.
    /// </remarks>
    public bool IgnoreInaccessible { get; set; } = true;
}
