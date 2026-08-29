using System.Diagnostics;
using System.IO.Abstractions;
using System.IO.Compression;

namespace EtlPipelines.Extensions.Files.Stages;

/// <summary>Extracts an archive into a directory.</summary>
public sealed class ExtractStage : IPipelineStage
{
    private readonly IFileSelection _archiveSelection;
    private readonly IFileInfo _archive;
    private readonly IDirectoryInfo _target;
    private readonly ExtractOptions _options;
    private readonly string _publishAs;

    /// <summary>Extracts <paramref name="archive"/> into <paramref name="target"/>.</summary>
    public ExtractStage(IFileInfo archive, IDirectoryInfo target, ExtractOptions options)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        _archive = archive;
        _archiveSelection = new SingleFileSelection(archive);
        _target = target;
        _options = options;
        Name = options.Name ?? $"extract {archive.Name}";
        _publishAs = options.PublishAs ?? Name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.StartNew();

        var resolved = await _archiveSelection.ResolveAsync(cancellationToken).ConfigureAwait(false);

        if (resolved.IsError)
        {
            return resolved.Errors;
        }

        var archive = resolved.Value[0];
        var format = _options.Format ?? ArchiveFormats.DetectFromName(archive.Name);

        if (_options.CreateTargetDirectory)
        {
            _target.Create();
        }

        var produced = new List<IFileInfo>();

        try
        {
            var outcome = format == ArchiveFormat.GZip
                ? await ExtractGZipAsync(archive, produced, cancellationToken).ConfigureAwait(false)
                : await ExtractContainerAsync(archive, format, produced, cancellationToken).ConfigureAwait(false);

            context.PublishProducedFiles(_publishAs, produced);

            if (outcome.IsError)
            {
                return outcome.Errors;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            context.PublishProducedFiles(_publishAs, produced);
            return Error.Failure($"files.extract.{Name}.failed", $"'{archive.Name}': {exception.Message}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    /// <summary>Gzip holds exactly one, unnamed file - its own name minus the <c>.gz</c> suffix.</summary>
    private async ValueTask<ErrorOr<Success>> ExtractGZipAsync(
        IFileInfo archive, List<IFileInfo> produced, CancellationToken cancellationToken)
    {
        var entryName = archive.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? archive.Name[..^3]
            : $"{archive.Name}.out";

        var target = _target.File(entryName);

        var written = await AtomicWrite.WriteAsync(
            target,
            _options.Overwrite,
            _options.CreateTargetDirectory,
            async (outStream, token) =>
            {
                await using var archiveStream = archive.OpenRead();
                await using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
                await gzip.CopyToAsync(outStream, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (written.IsError)
        {
            return written.Errors;
        }

        if (written.Value == FileWriteOutcome.Written)
        {
            produced.Add(target);
        }

        return Result.Success;
    }

    private async ValueTask<ErrorOr<Success>> ExtractContainerAsync(
        IFileInfo archive, ArchiveFormat format, List<IFileInfo> produced, CancellationToken cancellationToken)
    {
        // A first pass over the whole archive, checking every entry's safety and the MaxEntries/
        // MaxTotalBytes guards without writing anything - so an unsafe entry near the end of the
        // archive does not leave everything before it already on disk. Reads the archive twice
        // (a second, fresh stream for the real pass below); acceptable for a guard whose job is
        // safety, not speed.
        var validated = await ValidateEntriesAsync(archive, format, cancellationToken).ConfigureAwait(false);

        if (validated.IsError)
        {
            return validated.Errors;
        }

        await using var archiveStream = archive.OpenRead();

        return await ArchiveReader.ReadAsync(archiveStream, format, async (entry, data, token) =>
        {
            switch (entry.Kind)
            {
                case ArchiveEntryKind.Directory:
                {
                    var path = ArchivePath.Resolve(_target.FileSystem, _target.FullName, entry.Name);
                    _target.FileSystem.Directory.CreateDirectory(path.Value);
                    return Result.Success;
                }

                case ArchiveEntryKind.Link or ArchiveEntryKind.Other:
                    // Already ruled safe (or refused) by the validation pass above.
                    return Result.Success;

                default:
                {
                    var path = ArchivePath.Resolve(_target.FileSystem, _target.FullName, entry.Name);

                    if (data is null)
                    {
                        return Error.Failure(
                            $"files.extract.{Name}.failed", $"Entry '{entry.Name}' in '{archive.Name}' has no data.");
                    }

                    var entryTarget = _target.FileSystem.FileInfo.New(path.Value);

                    var written = await AtomicWrite.WriteAsync(
                        entryTarget,
                        _options.Overwrite,
                        _options.CreateTargetDirectory,
                        (outStream, writeToken) => new ValueTask(data.CopyToAsync(outStream, writeToken)),
                        token).ConfigureAwait(false);

                    if (written.IsError)
                    {
                        return written.Errors;
                    }

                    if (written.Value == FileWriteOutcome.Written)
                    {
                        produced.Add(entryTarget);
                    }

                    return Result.Success;
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ErrorOr<Success>> ValidateEntriesAsync(
        IFileInfo archive, ArchiveFormat format, CancellationToken cancellationToken)
    {
        var entryCount = 0;
        var totalBytes = 0L;

        await using var archiveStream = archive.OpenRead();

        return await ArchiveReader.ReadAsync(archiveStream, format, (entry, _, _) =>
        {
            entryCount++;

            if (entryCount > _options.MaxEntries)
            {
                return ValueTask.FromResult<ErrorOr<Success>>(Error.Failure(
                    $"files.extract.{Name}.too_many_entries",
                    $"'{archive.Name}' has more than {_options.MaxEntries} entries."));
            }

            if (entry.Kind is ArchiveEntryKind.Link or ArchiveEntryKind.Other)
            {
                return ValueTask.FromResult<ErrorOr<Success>>(_options.Links == LinkPolicy.Fail
                    ? Error.Failure(
                        $"files.extract.{Name}.unsafe_entry",
                        $"Entry '{entry.Name}' in '{archive.Name}' is a link or special entry, which extraction refuses to create.")
                    : Result.Success);
            }

            if (entry.Kind != ArchiveEntryKind.Directory)
            {
                totalBytes += entry.Length;

                if (_options.MaxTotalBytes > 0 && totalBytes > _options.MaxTotalBytes)
                {
                    return ValueTask.FromResult<ErrorOr<Success>>(Error.Failure(
                        $"files.extract.{Name}.too_large",
                        $"'{archive.Name}' exceeds the {_options.MaxTotalBytes}-byte extraction limit."));
                }
            }

            var path = ArchivePath.Resolve(_target.FileSystem, _target.FullName, entry.Name);

            return ValueTask.FromResult<ErrorOr<Success>>(path.IsError ? path.Errors : Result.Success);
        }, cancellationToken).ConfigureAwait(false);
    }
}
