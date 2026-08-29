using System.Diagnostics;
using System.IO.Abstractions;
using System.Net.Sockets;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Extensions.Files.Configuration;
using EtlPipelines.Extensions.Files.Selection;
using EtlPipelines.Extensions.Files.Sftp.Configuration;
using EtlPipelines.Extensions.Files.Sftp.Connections;
using EtlPipelines.Extensions.Files.Sftp.Selection;
using EtlPipelines.Extensions.Files.Writing;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace EtlPipelines.Extensions.Files.Sftp.Stages;

/// <summary>Downloads one remote file, or every remote file matching a pattern.</summary>
public sealed class SftpDownloadStage : IPipelineStage
{
    private readonly string _connectionName;
    private readonly string? _remotePath;
    private readonly SftpSelection? _selection;
    private readonly Func<string, IFileInfo> _targetFor;
    private readonly SftpDownloadOptions _options;
    private readonly string _publishAs;

    /// <summary>Downloads the single file at <paramref name="remotePath"/> to <paramref name="target"/>.</summary>
    public SftpDownloadStage(string connectionName, string remotePath, IFileInfo target, SftpDownloadOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        _connectionName = connectionName;
        _remotePath = remotePath;
        _targetFor = _ => target;
        _options = options;
        Name = options.Name ?? $"sftp download {connectionName}:{remotePath}";
        _publishAs = options.PublishAs ?? Name;
    }

    /// <summary>Downloads every file <paramref name="selection"/> matches into <paramref name="targetDirectory"/>.</summary>
    public SftpDownloadStage(
        string connectionName, SftpSelection selection, IDirectoryInfo targetDirectory, SftpDownloadOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ArgumentNullException.ThrowIfNull(options);

        _connectionName = connectionName;
        _selection = selection;
        _targetFor = name => targetDirectory.File(name);
        _options = options;
        Name = options.Name ?? $"sftp download {connectionName}:{selection.Directory}/{selection.Pattern}";
        _publishAs = options.PublishAs ?? Name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.StartNew();
        var factory = context.Services.GetRequiredSftpConnectionFactory(_connectionName);

        ISftpClient connectedClient;

        try
        {
            connectedClient = await factory.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SshException or SocketException or InvalidOperationException)
        {
            return Error.Failure($"sftp.download.{Name}.connect_failed", exception.Message);
        }

        using var client = connectedClient;

        ErrorOr<IReadOnlyList<string>> remotePaths;

        if (_remotePath is not null)
        {
            remotePaths = new List<string> { _remotePath };
        }
        else
        {
            remotePaths = await ListAsync(client, _selection!, cancellationToken).ConfigureAwait(false);
        }

        if (remotePaths.IsError)
        {
            return remotePaths.Errors;
        }

        var paths = remotePaths.Value;
        var outcomes = new (bool Ok, string? Message)?[paths.Count];

        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrency),
            CancellationToken = stopSource.Token,
        };

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count), parallelOptions, async (i, token) =>
            {
                try
                {
                    var outcome = await DownloadOneAsync(client, paths[i], token).ConfigureAwait(false);
                    outcomes[i] = outcome.IsError ? (false, outcome.FirstError.Description) : (true, null);
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

        for (var i = 0; i < paths.Count; i++)
        {
            if (outcomes[i] is not { } outcome)
            {
                continue;
            }

            if (outcome is { Ok: false, Message: { } message })
            {
                failures.Add($"{Name}: file {i + 1} of {paths.Count}, '{paths[i]}': {message}");
            }
            else
            {
                produced.Add(_targetFor(SftpPath.NameOf(paths[i])));
            }
        }

        context.PublishProducedFiles(_publishAs, produced);

        if (failures.Count > 0)
        {
            return _options.OnFileError == FileErrorAction.Stop
                ? Error.Failure($"sftp.download.{Name}.failed", failures[0])
                : Error.Failure(
                    $"sftp.download.{Name}.failed",
                    $"{failures.Count} of {paths.Count} files failed: {string.Join("; ", failures)}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    private async ValueTask<ErrorOr<FileWriteOutcome>> DownloadOneAsync(
        ISftpClient client, string remotePath, CancellationToken cancellationToken)
    {
        var target = _targetFor(SftpPath.NameOf(remotePath));

        var written = await AtomicWrite.WriteAsync(
            target,
            _options.Overwrite,
            _options.CreateTargetDirectory,
            (stream, token) => new ValueTask(client.DownloadFileAsync(remotePath, stream, token)),
            cancellationToken).ConfigureAwait(false);

        // Only once the local copy is complete and promoted - a failed download must never have
        // already removed the one remote copy that exists.
        if (!written.IsError && written.Value == FileWriteOutcome.Written && _options.DeleteRemoteAfterDownload)
        {
            await client.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private static async ValueTask<ErrorOr<IReadOnlyList<string>>> ListAsync(
        ISftpClient client, SftpSelection selection, CancellationToken cancellationToken)
    {
        var matches = new List<ISftpFile>();

        try
        {
            // The try must wrap the whole foreach, not just the call producing the enumerable:
            // ListDirectoryAsync throws on the first MoveNextAsync, not when it is called.
            await foreach (var entry in client.ListDirectoryAsync(selection.Directory, cancellationToken).ConfigureAwait(false))
            {
                if (entry.IsRegularFile
                    && FilePatterns.Matches(entry.Name, selection.Pattern, ignoreCase: false))
                {
                    matches.Add(entry);
                }
            }
        }
        catch (SftpPathNotFoundException exception)
        {
            return Error.Failure(
                "sftp.download.selection.missing", $"'{selection.Directory}': {exception.Message}");
        }

        IOrderedEnumerable<ISftpFile> ordered = selection.Order switch
        {
            FileOrder.LastWriteTime => matches.OrderBy(f => f.LastWriteTime),
            FileOrder.Length => matches.OrderBy(f => f.Length),
            _ => matches.OrderBy(f => f.FullName, StringComparer.Ordinal),
        };

        var sorted = selection.Descending ? ordered.Reverse() : ordered;

        return sorted.Select(f => f.FullName).ToArray();
    }
}
