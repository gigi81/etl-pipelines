using System.Formats.Tar;
using System.IO.Compression;

namespace EtlPipelines.Extensions.Files.Archives;

/// <summary>
/// Writes entries into a tar archive, never through <see cref="TarFile"/>. Also serves
/// <see cref="Configuration.ArchiveFormat.TarGZip"/>, by wrapping the tar stream in gzip compression
/// when the constructor is given a compression level.
/// </summary>
internal sealed class TarArchiveWriter : IArchiveWriter
{
    private readonly TarWriter _tar;
    private readonly GZipStream? _gzip;

    /// <summary>
    /// Opens <paramref name="archiveStream"/> as a new tar archive, compressed with gzip when
    /// <paramref name="gzipLevel"/> is given.
    /// </summary>
    public TarArchiveWriter(Stream archiveStream, CompressionLevel? gzipLevel)
    {
        if (gzipLevel is { } level)
        {
            _gzip = new GZipStream(archiveStream, level, leaveOpen: true);
            _tar = new TarWriter(_gzip, TarEntryFormat.Pax, leaveOpen: true);
        }
        else
        {
            _tar = new TarWriter(archiveStream, TarEntryFormat.Pax, leaveOpen: true);
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteEntryAsync(string entryName, Stream data, CancellationToken cancellationToken)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = data };
        await _tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The tar writer first: it still has to flush its trailer through the gzip stream that wraps
        // it, so the gzip stream must outlive it.
        await _tar.DisposeAsync().ConfigureAwait(false);

        if (_gzip is not null)
        {
            await _gzip.DisposeAsync().ConfigureAwait(false);
        }
    }
}
