namespace EtlPipelines.Extensions.Files.Sftp.Connections;

/// <summary>Remote path handling - always <c>/</c>-separated, regardless of the host OS this process runs on.</summary>
internal static class SftpPath
{
    /// <summary>The last segment of a remote path - the file name a local target is named after.</summary>
    public static string NameOf(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>Everything before the last segment of a remote path, or <see langword="null"/> for a bare file name.</summary>
    public static string? DirectoryOf(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? (slash == 0 ? "/" : null) : trimmed[..slash];
    }
}
