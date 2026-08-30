using System.IO.Compression;

namespace EtlPipelines.Extensions.Files.Archives;

/// <summary>Opens the <see cref="IArchiveWriter"/> for a given <see cref="Configuration.ArchiveFormat"/>.</summary>
internal static class ArchiveWriter
{
    /// <summary>Opens <paramref name="archiveStream"/> for writing in <paramref name="format"/>.</summary>
    public static IArchiveWriter Create(Stream archiveStream, ArchiveFormat format, CompressionLevel level) => format switch
    {
        ArchiveFormat.Zip => new ZipArchiveWriter(archiveStream, level),
        ArchiveFormat.Tar => new TarArchiveWriter(archiveStream, gzipLevel: null),
        ArchiveFormat.TarGZip => new TarArchiveWriter(archiveStream, level),
        ArchiveFormat.GZip => new GZipArchiveWriter(archiveStream, level),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
