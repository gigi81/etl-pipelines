using System.IO.Compression;

namespace EtlPipelines.Extensions.Files.Archives;

/// <summary>
/// Writes the one file bare <see cref="Configuration.ArchiveFormat.GZip"/> holds, directly through the
/// gzip stream - there are no named entries to address, so every "entry" written just extends the
/// same file.
/// </summary>
internal sealed class GZipArchiveWriter : IArchiveWriter
{
    private readonly GZipStream _gzip;

    /// <summary>Opens <paramref name="archiveStream"/> as a new gzip stream.</summary>
    public GZipArchiveWriter(Stream archiveStream, CompressionLevel level)
    {
        _gzip = new GZipStream(archiveStream, level, leaveOpen: true);
    }

    /// <inheritdoc />
    public ValueTask WriteEntryAsync(string entryName, Stream data, CancellationToken cancellationToken) =>
        new(data.CopyToAsync(_gzip, cancellationToken));

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _gzip.DisposeAsync();
}
