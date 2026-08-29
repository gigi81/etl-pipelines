namespace EtlPipelines.Files.Servers.Tests;

/// <summary>
/// The container images the file-server tests run against, pinned for the same reason
/// <c>EtlPipelines.Sql.Databases.Tests.ContainerImages</c> pins its own: a test run should only change
/// what it tests as a visible edit here, not as a side effect of a dependency bump.
/// </summary>
public static class ContainerImages
{
    /// <summary>
    /// Unmaintained upstream (last meaningful push around 2023) and multi-platform support is not
    /// guaranteed, but it remains the most widely used throwaway SFTP server for exactly this purpose
    /// and was confirmed to run - under emulation - on arm64 as well as the amd64 CI actually uses.
    /// </summary>
    public const string Sftp = "atmoz/sftp:alpine";
}
