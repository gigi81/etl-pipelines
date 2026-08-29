namespace EtlPipelines.Files.Configuration;

/// <summary>
/// An archive format these stages can read and write, all of them from the .NET shared framework -
/// this package takes no third-party dependency for compression.
/// </summary>
public enum ArchiveFormat
{
    /// <summary>A zip archive, read and written with <see cref="System.IO.Compression.ZipArchive"/>.</summary>
    Zip = 0,

    /// <summary>A tar archive, read and written with <see cref="System.Formats.Tar.TarReader"/> and <see cref="System.Formats.Tar.TarWriter"/>.</summary>
    Tar,

    /// <summary>A tar archive compressed with gzip - the two formats composed.</summary>
    TarGZip,

    /// <summary>A single file compressed with gzip. Holds exactly one entry; not a container format.</summary>
    GZip,
}
