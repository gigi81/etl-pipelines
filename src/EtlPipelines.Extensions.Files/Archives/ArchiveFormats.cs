namespace EtlPipelines.Extensions.Files.Archives;

/// <summary>Detects an <see cref="ArchiveFormat"/> from a file name, shared by compress and extract.</summary>
internal static class ArchiveFormats
{
    /// <summary>
    /// Detects the format from <paramref name="fileName"/>'s extension: <c>.tar.gz</c>/<c>.tgz</c> as
    /// <see cref="ArchiveFormat.TarGZip"/>, <c>.tar</c> as <see cref="ArchiveFormat.Tar"/>, <c>.gz</c>
    /// as <see cref="ArchiveFormat.GZip"/>, and anything else as <see cref="ArchiveFormat.Zip"/>.
    /// </summary>
    public static ArchiveFormat DetectFromName(string fileName) => fileName switch
    {
        _ when fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) => ArchiveFormat.TarGZip,
        _ when fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) => ArchiveFormat.TarGZip,
        _ when fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) => ArchiveFormat.Tar,
        _ when fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) => ArchiveFormat.GZip,
        _ => ArchiveFormat.Zip,
    };
}
