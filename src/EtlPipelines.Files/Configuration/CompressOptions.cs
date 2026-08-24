using System.IO.Compression;

namespace EtlPipelines.Files.Configuration;

/// <summary>Settings for compressing one file or every file matching a pattern into an archive.</summary>
public sealed class CompressOptions : FileSelectionOptions
{
    /// <summary>
    /// The archive format to write. Defaults to <see langword="null"/>, which detects the format from
    /// the archive file's extension (<c>.zip</c>, <c>.tar</c>, <c>.tar.gz</c>/<c>.tgz</c>, <c>.gz</c>).
    /// </summary>
    public ArchiveFormat? Format { get; set; }

    /// <summary>Compression level. Defaults to <see cref="CompressionLevel.Optimal"/>.</summary>
    public CompressionLevel Level { get; set; } = CompressionLevel.Optimal;

    /// <summary>
    /// Whether entries are named by file name alone rather than by their path relative to the
    /// selection's directory. Defaults to <see langword="false"/>, which preserves the directory
    /// structure the files were selected from.
    /// </summary>
    public bool FlattenPaths { get; set; }
}
