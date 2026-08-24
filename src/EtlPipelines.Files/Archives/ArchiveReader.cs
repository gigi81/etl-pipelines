using System.Formats.Tar;
using System.IO.Compression;

namespace EtlPipelines.Files.Archives;

/// <summary>
/// Reads zip and tar archives entry by entry, streaming throughout - never through
/// <see cref="System.IO.Compression.ZipFile"/> or <see cref="System.Formats.Tar.TarFile"/>, which are
/// path-based and would bypass whatever <see cref="System.IO.Abstractions.IFileSystem"/> the caller is
/// using. Bare <see cref="Configuration.ArchiveFormat.GZip"/> is not a container format and has no
/// entries to iterate, so it is handled directly by the extraction stage instead of here.
/// </summary>
internal static class ArchiveReader
{
    /// <summary>
    /// Reads every entry of <paramref name="archiveStream"/>, calling <paramref name="onEntry"/> for
    /// each one with its data stream - non-<see langword="null"/> only for
    /// <see cref="ArchiveEntryKind.RegularFile"/> entries. Stops at the first error <paramref
    /// name="onEntry"/> returns.
    /// </summary>
    public static ValueTask<ErrorOr<Success>> ReadAsync(
        Stream archiveStream,
        ArchiveFormat format,
        Func<ArchiveEntry, Stream?, CancellationToken, ValueTask<ErrorOr<Success>>> onEntry,
        CancellationToken cancellationToken) =>
        format switch
        {
            ArchiveFormat.Zip => ReadZipAsync(archiveStream, onEntry, cancellationToken),
            ArchiveFormat.Tar => ReadTarAsync(archiveStream, onEntry, cancellationToken),
            ArchiveFormat.TarGZip => ReadTarGZipAsync(archiveStream, onEntry, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(format), format, $"{format} has no entries to iterate; the caller must handle it directly."),
        };

    private static async ValueTask<ErrorOr<Success>> ReadZipAsync(
        Stream stream,
        Func<ArchiveEntry, Stream?, CancellationToken, ValueTask<ErrorOr<Success>>> onEntry,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kind = ClassifyZipEntry(entry);
            var info = new ArchiveEntry(entry.FullName, kind, entry.Length);

            ErrorOr<Success> result;

            if (kind == ArchiveEntryKind.RegularFile)
            {
                await using var data = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                result = await onEntry(info, data, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await onEntry(info, null, cancellationToken).ConfigureAwait(false);
            }

            if (result.IsError)
            {
                return result.Errors;
            }
        }

        return Result.Success;
    }

    private static ArchiveEntryKind ClassifyZipEntry(ZipArchiveEntry entry)
    {
        if (entry.FullName.EndsWith('/') && entry.Length == 0 && entry.CompressedLength == 0)
        {
            return ArchiveEntryKind.Directory;
        }

        // The Unix file mode a *nix zip tool wrote lives in the high 16 bits of ExternalAttributes;
        // 0xA000 (S_IFLNK) marks a symbolic link. Zip has no dedicated link entry type - it fakes one
        // this way, which is exactly why Zip Slip guards that only check names miss it.
        var unixMode = unchecked((uint)entry.ExternalAttributes) >> 16;

        return (unixMode & 0xF000) == 0xA000 ? ArchiveEntryKind.Link : ArchiveEntryKind.RegularFile;
    }

    private static async ValueTask<ErrorOr<Success>> ReadTarAsync(
        Stream stream,
        Func<ArchiveEntry, Stream?, CancellationToken, ValueTask<ErrorOr<Success>>> onEntry,
        CancellationToken cancellationToken)
    {
        await using var reader = new TarReader(stream, leaveOpen: true);

        TarEntry? entry;

        while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)) != null)
        {
            var kind = ClassifyTarEntry(entry.EntryType);
            var info = new ArchiveEntry(entry.Name, kind, entry.Length);

            // DataStream is a window over the underlying archive stream and is only valid until the
            // next GetNextEntryAsync call, which is why it is handed straight to onEntry rather than
            // returned for the caller to read later - by the time the loop advances it is gone.
            var result = await onEntry(
                info,
                kind == ArchiveEntryKind.RegularFile ? entry.DataStream : null,
                cancellationToken).ConfigureAwait(false);

            if (result.IsError)
            {
                return result.Errors;
            }
        }

        return Result.Success;
    }

    private static ArchiveEntryKind ClassifyTarEntry(TarEntryType type) => type switch
    {
        TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile
            => ArchiveEntryKind.RegularFile,
        TarEntryType.Directory or TarEntryType.DirectoryList
            => ArchiveEntryKind.Directory,
        TarEntryType.SymbolicLink or TarEntryType.HardLink or TarEntryType.RenamedOrSymlinked
            => ArchiveEntryKind.Link,
        _ => ArchiveEntryKind.Other,
    };

    private static async ValueTask<ErrorOr<Success>> ReadTarGZipAsync(
        Stream stream,
        Func<ArchiveEntry, Stream?, CancellationToken, ValueTask<ErrorOr<Success>>> onEntry,
        CancellationToken cancellationToken)
    {
        // Decompression only, not seeking: gzip streams are read forward-only, which is also why
        // TarReader is asked for copyData: false here - copying eagerly would mean buffering an
        // entry that can no longer be re-read from its source.
        await using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        return await ReadTarAsync(gzip, onEntry, cancellationToken).ConfigureAwait(false);
    }
}
