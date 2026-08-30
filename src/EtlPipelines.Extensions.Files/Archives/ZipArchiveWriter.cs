using System.IO.Compression;

namespace EtlPipelines.Extensions.Files.Archives;

/// <summary>Writes entries into a zip archive, never through <see cref="ZipFile"/>.</summary>
internal sealed class ZipArchiveWriter : IArchiveWriter
{
    private readonly ZipArchive _zip;
    private readonly CompressionLevel _level;

    /// <summary>Opens <paramref name="archiveStream"/> as a new zip archive.</summary>
    public ZipArchiveWriter(Stream archiveStream, CompressionLevel level)
    {
        _zip = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true);
        _level = level;
    }

    /// <inheritdoc />
    public async ValueTask WriteEntryAsync(string entryName, Stream data, CancellationToken cancellationToken)
    {
        var entry = _zip.CreateEntry(entryName, _level);
        await using var target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await data.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _zip.DisposeAsync();
}
