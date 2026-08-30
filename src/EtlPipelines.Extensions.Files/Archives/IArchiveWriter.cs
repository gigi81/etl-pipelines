namespace EtlPipelines.Extensions.Files.Archives;

/// <summary>
/// Writes one archive, entry by entry, streaming throughout. What <see cref="ArchiveWriter.Create"/>
/// returns for a given <see cref="Configuration.ArchiveFormat"/>.
/// </summary>
internal interface IArchiveWriter : IAsyncDisposable
{
    /// <summary>
    /// Writes one entry from <paramref name="data"/>. The caller owns <paramref name="data"/> and
    /// disposes it; this only reads from it.
    /// </summary>
    ValueTask WriteEntryAsync(string entryName, Stream data, CancellationToken cancellationToken);
}
