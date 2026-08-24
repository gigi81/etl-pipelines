namespace EtlPipelines.Files.Configuration;

/// <summary>
/// What a multi-file selection is sorted by before a stage acts on it.
/// </summary>
/// <remarks>
/// Enumeration order is filesystem-defined and differs between Windows, Linux and
/// <c>MockFileSystem</c>. Sorting is what makes a rerun do the same work in the same order, and what
/// makes "file 3 of 5" in an error message a real, reproducible address.
/// </remarks>
public enum FileOrder
{
    /// <summary>By file name, ordinally. The default.</summary>
    Name = 0,

    /// <summary>By last-write time.</summary>
    LastWriteTime,

    /// <summary>By file size, in bytes.</summary>
    Length,
}
