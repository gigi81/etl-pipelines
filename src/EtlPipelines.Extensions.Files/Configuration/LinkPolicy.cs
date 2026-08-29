namespace EtlPipelines.Files.Configuration;

/// <summary>
/// What extraction does with a tar entry that names a symbolic or hard link, or a device or FIFO
/// special file.
/// </summary>
/// <remarks>
/// A link is the other half of Zip Slip: a link entry aimed at <c>/etc/passwd</c>, followed by a
/// regular entry writing "through" it by name, escapes a containment check that only looks at entry
/// names. Neither policy here ever creates a link - the choice is only whether the archive as a
/// whole is refused for containing one.
/// </remarks>
public enum LinkPolicy
{
    /// <summary>Skip the entry and continue extracting the rest of the archive. The default.</summary>
    Skip = 0,

    /// <summary>Fail the whole extraction the first time a link or special entry is found.</summary>
    Fail,
}
