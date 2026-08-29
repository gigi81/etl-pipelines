namespace EtlPipelines.Extensions.Files.Configuration;

/// <summary>Settings for moving one file or every file matching a pattern.</summary>
public sealed class FileMoveOptions : FileSelectionOptions
{
    /// <summary>
    /// Whether the move is a copy-to-temporary-sibling-then-rename-then-delete-source, rather than a
    /// plain rename. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// A same-volume <c>IFileInfo.MoveTo</c> already is an atomic rename, so wrapping it in a copy
    /// would add a full data copy to the operation people choose precisely because it is cheap. Set
    /// this to <see langword="true"/> when the destination is a network share: <c>File.Move</c> falls
    /// back to copy-then-delete across volumes regardless, and a UNC path is always a different
    /// volume, so a plain move there is already a copy and a delete - just not an atomic one. This
    /// option buys back the atomicity, at the cost of the extra copy it was already paying.
    /// </remarks>
    public bool WriteAtomically { get; set; }
}
