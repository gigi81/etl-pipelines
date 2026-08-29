using System.Diagnostics;
using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Files.Stages;

/// <summary>Compresses one file, or every file a selection matches, into an archive.</summary>
public sealed class CompressStage : IPipelineStage
{
    private readonly IFileSelection _selection;
    private readonly IDirectoryInfo? _baseDirectory;
    private readonly IFileInfo _archive;
    private readonly CompressOptions _options;
    private readonly string _publishAs;

    /// <summary>
    /// Compresses whatever <paramref name="selection"/> resolves to into <paramref name="archive"/>.
    /// </summary>
    /// <param name="selection">The file or files to compress.</param>
    /// <param name="baseDirectory">
    /// The directory entry names are computed relative to, so the archive preserves the structure
    /// files were selected from. <see langword="null"/> for a single file, whose entry is always
    /// named by its own file name.
    /// </param>
    /// <param name="archive">The archive file to write.</param>
    /// <param name="options">Format and selection settings.</param>
    public CompressStage(IFileSelection selection, IDirectoryInfo? baseDirectory, IFileInfo archive, CompressOptions options)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(options);

        _selection = selection;
        _baseDirectory = baseDirectory;
        _archive = archive;
        _options = options;
        Name = options.Name ?? $"compress -> {archive.Name}";
        _publishAs = options.PublishAs ?? Name;
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
        var format = _options.Format ?? ArchiveFormats.DetectFromName(_archive.Name);

        if (format is ArchiveFormat.GZip && sources.Count != 1)
        {
            return Error.Failure(
                $"files.compress.{Name}.gzip_single_file",
                $"gzip holds exactly one file; the selection matched {sources.Count}.");
        }

        try
        {
            var written = await AtomicWrite.WriteAsync(
                _archive,
                _options.Overwrite,
                _options.CreateTargetDirectory,
                async (archiveStream, token) =>
                {
                    var writer = ArchiveWriter.Create(archiveStream, format, _options.Level);

                    await using (writer.ConfigureAwait(false))
                    {
                        foreach (var source in sources)
                        {
                            token.ThrowIfCancellationRequested();

                            var entryName = EntryNameFor(source);
                            await using var data = source.OpenRead();
                            await writer.WriteEntryAsync(entryName, data, token).ConfigureAwait(false);
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (written.IsError)
            {
                return written.Errors;
            }

            var produced = written.Value == FileWriteOutcome.Written
                ? new IFileInfo[] { _archive }
                : [];

            context.PublishProducedFiles(_publishAs, produced);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.PublishProducedFiles(_publishAs, []);
            return Error.Failure($"files.compress.{Name}.failed", exception.Message);
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    private string EntryNameFor(IFileInfo source)
    {
        if (_options.FlattenPaths || _baseDirectory is null)
        {
            return source.Name;
        }

        var relative = source.FileSystem.Path.GetRelativePath(_baseDirectory.FullName, source.FullName);
        return relative.Replace(source.FileSystem.Path.DirectorySeparatorChar, '/');
    }
}
