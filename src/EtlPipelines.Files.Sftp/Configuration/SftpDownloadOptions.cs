using EtlPipelines.Files.Configuration;

namespace EtlPipelines.Files.Sftp.Configuration;

/// <summary>Settings for downloading one or more files over SFTP.</summary>
public sealed class SftpDownloadOptions : FileWriteOptions
{
    /// <summary>
    /// Whether the remote file is deleted once its local copy has been promoted. Defaults to
    /// <see langword="false"/>. The remote file is deleted only after the local write completes, so a
    /// failed download never removes a file that was not actually copied.
    /// </summary>
    public bool DeleteRemoteAfterDownload { get; set; }
}
