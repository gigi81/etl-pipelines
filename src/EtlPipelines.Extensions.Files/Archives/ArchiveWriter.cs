using System.Formats.Tar;
using System.IO.Compression;

namespace EtlPipelines.Files.Archives;

/// <summary>
/// Writes zip and tar archives entry by entry, streaming throughout - never through
/// <see cref="System.IO.Compression.ZipFile"/> or <see cref="System.Formats.Tar.TarFile"/>. Bare
/// <see cref="Configuration.ArchiveFormat.GZip"/> writes its one file directly through the same gzip
/// stream, since it holds no named entries.
/// </summary>
internal sealed class ArchiveWriter : IAsyncDisposable
{
    private readonly ArchiveFormat _format;
    private readonly CompressionLevel _level;
    private readonly ZipArchive? _zip;
    private readonly TarWriter? _tar;
    private readonly GZipStream? _gzip;

    private ArchiveWriter(ArchiveFormat format, CompressionLevel level, ZipArchive? zip, TarWriter? tar, GZipStream? gzip)
    {
        _format = format;
        _level = level;
        _zip = zip;
        _tar = tar;
        _gzip = gzip;
    }

    /// <summary>Opens <paramref name="archiveStream"/> for writing in <paramref name="format"/>.</summary>
    public static ArchiveWriter Create(Stream archiveStream, ArchiveFormat format, CompressionLevel level)
    {
        switch (format)
        {
            case ArchiveFormat.Zip:
                return new ArchiveWriter(format, level, new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true), null, null);

            case ArchiveFormat.Tar:
                return new ArchiveWriter(format, level, null, new TarWriter(archiveStream, TarEntryFormat.Pax, leaveOpen: true), null);

            case ArchiveFormat.TarGZip:
                var gzipForTar = new GZipStream(archiveStream, level, leaveOpen: true);
                return new ArchiveWriter(format, level, null, new TarWriter(gzipForTar, TarEntryFormat.Pax, leaveOpen: true), gzipForTar);

            case ArchiveFormat.GZip:
                return new ArchiveWriter(format, level, null, null, new GZipStream(archiveStream, level, leaveOpen: true));

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    /// <summary>
    /// Writes one entry from <paramref name="data"/>. The caller owns <paramref name="data"/> and
    /// disposes it; this only reads from it.
    /// </summary>
    public async ValueTask WriteEntryAsync(string entryName, Stream data, CancellationToken cancellationToken)
    {
        switch (_format)
        {
            case ArchiveFormat.Zip:
                var entry = _zip!.CreateEntry(entryName, _level);
                await using (var target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    await data.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                break;

            case ArchiveFormat.Tar:
            case ArchiveFormat.TarGZip:
                var tarEntry = new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = data };
                await _tar!.WriteEntryAsync(tarEntry, cancellationToken).ConfigureAwait(false);
                break;

            case ArchiveFormat.GZip:
                await data.CopyToAsync(_gzip!, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The tar writer first: it still has to flush its trailer through the gzip stream that wraps
        // it, so the gzip stream must outlive it. The zip archive writes its own central directory on
        // disposal, independent of the others.
        if (_tar is not null)
        {
            await _tar.DisposeAsync().ConfigureAwait(false);
        }

        if (_zip is not null)
        {
            await _zip.DisposeAsync().ConfigureAwait(false);
        }

        if (_gzip is not null)
        {
            await _gzip.DisposeAsync().ConfigureAwait(false);
        }
    }
}
