using System.Diagnostics;
using System.IO.Abstractions;
using System.Net.Sockets;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Extensions.Files.Configuration;
using EtlPipelines.Extensions.Files.Selection;
using EtlPipelines.Extensions.Files.Sftp.Configuration;
using EtlPipelines.Extensions.Files.Sftp.Connections;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace EtlPipelines.Extensions.Files.Sftp.Stages;

/// <summary>Uploads one local file, or every file a selection matches, over SFTP.</summary>
public sealed class SftpUploadStage : IPipelineStage
{
    private readonly string _connectionName;
    private readonly IFileSelection _selection;
    private readonly Func<IFileInfo, string> _remotePathFor;
    private readonly SftpUploadOptions _options;
    private readonly string _publishAs;

    /// <summary>Uploads whatever <paramref name="selection"/> resolves to, computing each file's remote path with <paramref name="remotePathFor"/>.</summary>
    internal SftpUploadStage(
        string connectionName, IFileSelection selection, Func<IFileInfo, string> remotePathFor,
        SftpUploadOptions options, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(remotePathFor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _connectionName = connectionName;
        _selection = selection;
        _remotePathFor = remotePathFor;
        _options = options;
        Name = name;
        _publishAs = options.PublishAs ?? name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.StartNew();

        var resolved = await _selection.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (resolved.IsError)
        {
            return resolved.Errors;
        }

        var sources = resolved.Value;
        var factory = context.Services.GetRequiredSftpConnectionFactory(_connectionName);

        ISftpClient connectedClient;

        try
        {
            connectedClient = await factory.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SshException or SocketException or InvalidOperationException)
        {
            return Error.Failure($"sftp.upload.{Name}.connect_failed", exception.Message);
        }

        using var client = connectedClient;

        var outcomes = new (bool Ok, string? Message)?[sources.Count];

        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrency),
            CancellationToken = stopSource.Token,
        };

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, sources.Count), parallelOptions, async (i, token) =>
            {
                try
                {
                    await UploadOneAsync(client, sources[i], _remotePathFor(sources[i]), token).ConfigureAwait(false);
                    outcomes[i] = (true, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SshException or SocketException or InvalidOperationException)
                {
                    outcomes[i] = (false, ex.Message);
                }

                if (outcomes[i] is { Ok: false } && _options.OnFileError == FileErrorAction.Stop)
                {
                    await stopSource.CancelAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own stop-on-first-failure firing, not the caller's cancellation.
        }

        var produced = new List<IFileInfo>();
        var failures = new List<string>();

        for (var i = 0; i < sources.Count; i++)
        {
            if (outcomes[i] is not { } outcome)
            {
                continue;
            }

            if (outcome is { Ok: false, Message: { } message })
            {
                failures.Add($"{Name}: file {i + 1} of {sources.Count}, '{sources[i].Name}': {message}");
            }
            else
            {
                produced.Add(sources[i]);
            }
        }

        context.PublishProducedFiles(_publishAs, produced);

        if (failures.Count > 0)
        {
            return _options.OnFileError == FileErrorAction.Stop
                ? Error.Failure($"sftp.upload.{Name}.failed", failures[0])
                : Error.Failure(
                    $"sftp.upload.{Name}.failed",
                    $"{failures.Count} of {sources.Count} files failed: {string.Join("; ", failures)}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    private async Task UploadOneAsync(ISftpClient client, IFileInfo source, string remotePath, CancellationToken cancellationToken)
    {
        if (_options.CreateTargetDirectory)
        {
            var remoteDirectory = SftpPath.DirectoryOf(remotePath);

            if (remoteDirectory is not null && !await client.ExistsAsync(remoteDirectory, cancellationToken).ConfigureAwait(false))
            {
                await client.CreateDirectoryAsync(remoteDirectory, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_options.Overwrite != OverwritePolicy.Overwrite
            && await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            if (_options.Overwrite == OverwritePolicy.Skip)
            {
                return;
            }

            throw new InvalidOperationException($"'{remotePath}' already exists.");
        }

        await using var stream = source.OpenRead();
        await client.UploadFileAsync(stream, remotePath, canOverride: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
