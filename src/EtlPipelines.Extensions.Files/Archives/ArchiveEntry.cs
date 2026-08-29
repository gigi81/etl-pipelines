namespace EtlPipelines.Files.Archives;

/// <summary>What kind of thing one archive entry represents, unified across the formats this package reads.</summary>
internal enum ArchiveEntryKind
{
    /// <summary>An ordinary file, with data to extract.</summary>
    RegularFile,

    /// <summary>A directory entry, carrying no data.</summary>
    Directory,

    /// <summary>A symbolic or hard link. Tar only - zip has no portable link entry type.</summary>
    Link,

    /// <summary>A device, FIFO, or anything else this package does not extract.</summary>
    Other,
}

/// <summary>One entry read from an archive, with its data - if it has any - already positioned to read.</summary>
internal readonly record struct ArchiveEntry(string Name, ArchiveEntryKind Kind, long Length);
