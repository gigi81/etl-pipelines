using System.IO.Abstractions;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Extensions.Files.Selection;
using EtlPipelines.Extensions.Files.Sftp.Configuration;
using EtlPipelines.Extensions.Files.Sftp.Selection;
using EtlPipelines.Extensions.Files.Sftp.Stages;

// ReSharper disable once CheckNamespace
namespace EtlPipelines;

/// <summary>Downloading and uploading files over SFTP as pipeline stages.</summary>
public static class ExtensionsFilesSftp
{
    /// <summary>Downloads the single remote file at <paramref name="remotePath"/>.</summary>
    public static IPipelineBuilder DownloadFromSftp(
        this IPipelineBuilder builder,
        string connectionName,
        string remotePath,
        IFileInfo target,
        Action<SftpDownloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SftpDownloadOptions();
        configure?.Invoke(options);

        return builder.AddStage(new SftpDownloadStage(connectionName, remotePath, target, options));
    }

    /// <summary>Downloads every remote file matching <paramref name="pattern"/> in <paramref name="remoteDirectory"/>.</summary>
    public static IPipelineBuilder DownloadFromSftp(
        this IPipelineBuilder builder,
        string connectionName,
        string remoteDirectory,
        string pattern,
        IDirectoryInfo target,
        Action<SftpDownloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var options = new SftpDownloadOptions();
        configure?.Invoke(options);

        var selection = new SftpSelection(remoteDirectory, pattern)
        {
            Recursive = false,
        };

        return builder.DownloadFromSftp(connectionName, selection, target, options);
    }

    /// <summary>Downloads every remote file <paramref name="remote"/> matches.</summary>
    public static IPipelineBuilder DownloadFromSftp(
        this IPipelineBuilder builder,
        string connectionName,
        SftpSelection remote,
        IDirectoryInfo target,
        Action<SftpDownloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SftpDownloadOptions();
        configure?.Invoke(options);

        return builder.DownloadFromSftp(connectionName, remote, target, options);
    }

    private static IPipelineBuilder DownloadFromSftp(
        this IPipelineBuilder builder,
        string connectionName,
        SftpSelection remote,
        IDirectoryInfo target,
        SftpDownloadOptions options)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(target);

        return builder.AddStage(new SftpDownloadStage(connectionName, remote, target, options));
    }

    /// <summary>Uploads one local file to <paramref name="remotePath"/>.</summary>
    public static IPipelineBuilder UploadToSftp(
        this IPipelineBuilder builder,
        string connectionName,
        IFileInfo source,
        string remotePath,
        Action<SftpUploadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        var options = new SftpUploadOptions();
        configure?.Invoke(options);

        var name = options.Name ?? $"sftp upload {source.Name} -> {connectionName}:{remotePath}";

        return builder.AddStage(new SftpUploadStage(
            connectionName, new SingleFileSelection(source), _ => remotePath, options, name));
    }

    /// <summary>Uploads every local file matching <paramref name="pattern"/> in <paramref name="source"/> into <paramref name="remoteDirectory"/>.</summary>
    public static IPipelineBuilder UploadToSftp(
        this IPipelineBuilder builder,
        string connectionName,
        IDirectoryInfo source,
        string pattern,
        string remoteDirectory,
        Action<SftpUploadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var options = new SftpUploadOptions();
        configure?.Invoke(options);

        return builder.UploadToSftp(connectionName, new PatternFileSelection(source, pattern, options), remoteDirectory, options);
    }

    /// <summary>Uploads every local file in <paramref name="selection"/> into <paramref name="remoteDirectory"/>.</summary>
    public static IPipelineBuilder UploadToSftp(
        this IPipelineBuilder builder,
        string connectionName,
        IFileSelection selection,
        string remoteDirectory,
        Action<SftpUploadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SftpUploadOptions();
        configure?.Invoke(options);

        return builder.UploadToSftp(connectionName, selection, remoteDirectory, options);
    }

    private static IPipelineBuilder UploadToSftp(
        this IPipelineBuilder builder,
        string connectionName,
        IFileSelection selection,
        string remoteDirectory,
        SftpUploadOptions options)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteDirectory);

        var trimmedDirectory = remoteDirectory.TrimEnd('/');
        var name = options.Name ?? $"sftp upload {selection.Name} -> {connectionName}:{remoteDirectory}";

        return builder.AddStage(new SftpUploadStage(
            connectionName, selection, source => $"{trimmedDirectory}/{source.Name}", options, name));
    }
}
