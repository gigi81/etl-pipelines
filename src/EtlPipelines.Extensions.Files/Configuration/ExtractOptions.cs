namespace EtlPipelines.Extensions.Files.Configuration;

/// <summary>Settings for extracting an archive.</summary>
public sealed class ExtractOptions : FileWriteOptions
{
    /// <summary>
    /// The archive format to read. Defaults to <see langword="null"/>, which detects the format from
    /// the archive file's extension (<c>.zip</c>, <c>.tar</c>, <c>.tar.gz</c>/<c>.tgz</c>, <c>.gz</c>).
    /// </summary>
    public ArchiveFormat? Format { get; set; }

    /// <summary>What to do with a symbolic link, hard link, or device/FIFO entry. Defaults to <see cref="LinkPolicy.Skip"/>.</summary>
    public LinkPolicy Links { get; set; } = LinkPolicy.Skip;

    /// <summary>
    /// The most entries an archive may contain before extraction is refused. Defaults to 10,000, as a
    /// zip-bomb guard rather than a limit a legitimate feed is expected to bump into.
    /// </summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>
    /// The most total uncompressed bytes an archive may extract to before extraction is refused.
    /// Defaults to 0, meaning unlimited - a legitimate ETL archive really can be tens of gigabytes,
    /// and a false limit that fires at three in the morning is worse than the risk this guards
    /// against for a trusted vendor feed. Both this and <see cref="MaxEntries"/> are belt-and-braces,
    /// not a substitute for trusting the source of the archive.
    /// </summary>
    public long MaxTotalBytes { get; set; }
}
