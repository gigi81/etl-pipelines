using System.Diagnostics;
using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Files.Stages;

/// <summary>The knobs a <see cref="FileTransferStage"/> needs, gathered from whichever of
/// <see cref="FileCopyOptions"/> or <see cref="FileMoveOptions"/> the caller configured.</summary>
internal readonly record struct FileTransferSettings(
    OverwritePolicy Overwrite,
    bool CreateTargetDirectory,
    bool WriteAtomically,
    FileErrorAction OnFileError,
    int MaxConcurrency);

/// <summary>Copies or moves one file, or every file a selection matches, into a target.</summary>
/// <remarks>
/// One class covers both directions, the same way <c>SqlCommandStage</c> covers a statement and a
/// stored procedure call: copy and move differ only in whether the source survives, and in how a
/// plain (non-atomic) move is performed - everything else, including the overwrite policy and the
/// per-file failure handling, is identical.
/// </remarks>
public sealed class FileTransferStage : IPipelineStage
{
    private readonly FileTransferMode _mode;
    private readonly IFileSelection _selection;
    private readonly Func<IFileInfo, IFileInfo> _target;
    private readonly FileTransferSettings _settings;
    private readonly string _publishAs;

    /// <summary>
    /// Transfers whatever <paramref name="selection"/> resolves to, computing each file's
    /// destination with <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Constructed only through the <c>CopyFile</c>/<c>CopyFiles</c>/<c>MoveFile</c>/<c>MoveFiles</c>
    /// extension methods, which are what fixes <see cref="FileTransferSettings"/>'s meaning for each
    /// of copy and move - this type does not stand alone as public API.
    /// </remarks>
    internal FileTransferStage(
        FileTransferMode mode,
        IFileSelection selection,
        Func<IFileInfo, IFileInfo> target,
        FileTransferSettings settings,
        string name,
        string? publishAs = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _mode = mode;
        _selection = selection;
        _target = target;
        _settings = settings;
        Name = name;
        _publishAs = publishAs ?? name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.StartNew();
        var verb = _mode == FileTransferMode.Copy ? "copy" : "move";

        var resolved = await _selection.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (resolved.IsError)
        {
            return resolved.Errors;
        }

        var sources = resolved.Value;

        // Fail means the whole batch of targets must be new. Checked before a single byte is
        // written, so failing on file 4 of 5 never leaves the first three clobbered.
        if (_settings.Overwrite == OverwritePolicy.Fail)
        {
            foreach (var source in sources)
            {
                var target = _target(source);
                target.Refresh();

                if (target.Exists)
                {
                    return Error.Failure($"files.{verb}.{Name}.exists", $"'{target.FullName}' already exists.");
                }
            }
        }

        var outcomes = new (bool Ok, string? Message)?[sources.Count];

        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _settings.MaxConcurrency),
            CancellationToken = stopSource.Token,
        };

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, sources.Count), parallelOptions, async (i, token) =>
            {
                var source = sources[i];
                var target = _target(source);

                try
                {
                    var outcome = await TransferOneAsync(source, target, token).ConfigureAwait(false);
                    outcomes[i] = outcome.IsError ? (false, outcome.FirstError.Description) : (true, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    outcomes[i] = (false, ex.Message);
                }

                // Above degree-of-parallelism 1, "stop" means "cancel the rest and report the first
                // failure observed" - ordering only governs the order work was started in.
                if (outcomes[i] is { Ok: false } && _settings.OnFileError == FileErrorAction.Stop)
                {
                    await stopSource.CancelAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own stop-on-first-failure firing, not the caller's cancellation - the outcomes
            // array already has what it needs; a real caller cancellation is left to propagate.
        }

        var produced = new List<IFileInfo>();
        var failures = new List<string>();

        for (var i = 0; i < sources.Count; i++)
        {
            // Never scheduled, because Stop cancelled the rest before it was reached.
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
                produced.Add(_target(sources[i]));
            }
        }

        context.PublishProducedFiles(_publishAs, produced);

        if (failures.Count > 0)
        {
            return _settings.OnFileError == FileErrorAction.Stop
                ? Error.Failure($"files.{verb}.{Name}.failed", failures[0])
                : Error.Failure(
                    $"files.{verb}.{Name}.failed",
                    $"{failures.Count} of {sources.Count} files failed: {string.Join("; ", failures)}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    private async ValueTask<ErrorOr<FileWriteOutcome>> TransferOneAsync(
        IFileInfo source, IFileInfo target, CancellationToken cancellationToken)
    {
        if (_mode == FileTransferMode.Move && !_settings.WriteAtomically)
        {
            // A same-volume rename already is atomic; this is the cheap path move exists for.
            if (AtomicWrite.CheckOverwrite(target, _settings.Overwrite) is { } settled)
            {
                return settled;
            }

            if (_settings.CreateTargetDirectory)
            {
                target.Directory?.Create();
            }

            source.MoveTo(target.FullName, overwrite: true);
            return FileWriteOutcome.Written;
        }

        var outcome = await AtomicWrite.WriteAsync(
            target,
            _settings.Overwrite,
            _settings.CreateTargetDirectory,
            async (stream, token) =>
            {
                await using var read = source.OpenRead();
                await read.CopyToAsync(stream, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (_mode == FileTransferMode.Move && !outcome.IsError && outcome.Value == FileWriteOutcome.Written)
        {
            // Only once the destination is complete and promoted - a failed multi-file move must
            // leave the remaining sources in place, not delete one whose copy never finished.
            source.Delete();
        }

        return outcome;
    }
}
