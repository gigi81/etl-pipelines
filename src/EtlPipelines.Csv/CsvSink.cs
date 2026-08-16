using System.IO.Abstractions;
using CsvHelper;

namespace EtlPipelines.Csv;

/// <summary>
/// Writes rows to a CSV file.
/// </summary>
/// <remarks>
/// <para>
/// By default the rows stream into a temporary file beside the target, which is renamed into place
/// from <see cref="CompleteAsync"/> — the hook the runtime calls exactly once, and only when the run
/// succeeded. The target path therefore either does not exist or holds a whole file; a downstream job
/// can never pick up a truncated one. A failed run leaves the temporary file behind for inspection.
/// </para>
/// <para>
/// This is the reason <see cref="IAsyncCompletable"/> exists separately from
/// <see cref="IAsyncDisposable"/>: disposal also runs on the failure path, where promoting a
/// half-written file is precisely the wrong thing to do.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to write.</typeparam>
public sealed class CsvSink<TRow> : IDataSink<TRow>, IAsyncInitializable, IAsyncCompletable
{
    private readonly IFileSystem _fileSystem;
    private readonly string? _targetPath;
    private readonly CsvSinkOptions _options;
    private readonly bool _ownsWriter;

    private TextWriter? _writer;
    private CsvWriter? _csv;
    private string? _writingTo;

    /// <summary>Writes to the file at <paramref name="path"/>.</summary>
    /// <param name="path">Destination path. Overwritten if it already exists.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    /// <param name="fileSystem">
    /// The filesystem to write through. Defaults to the real one; pass a <c>MockFileSystem</c> to
    /// test against an in-memory filesystem instead.
    /// </param>
    public CsvSink(string path, CsvSinkOptions? options = null, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _fileSystem = fileSystem ?? new FileSystem();
        _targetPath = _fileSystem.Path.GetFullPath(path);
        _options = options ?? new CsvSinkOptions();
        _ownsWriter = true;
    }

    /// <summary>
    /// Writes to an existing <see cref="TextWriter"/>.
    /// </summary>
    /// <param name="writer">Destination writer.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the writer.</param>
    /// <remarks>
    /// There is no path to rename here, so the atomic behaviour does not apply: rows reach the writer
    /// as they are produced, and completion only flushes. A caller who needs all-or-nothing must
    /// arrange it themselves, or use the path-based constructor.
    /// </remarks>
    public CsvSink(TextWriter writer, CsvSinkOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // Nothing here touches the filesystem, but the field is non-nullable for the path-based flow.
        _fileSystem = new FileSystem();
        _writer = writer;
        _options = options ?? new CsvSinkOptions();
        _ownsWriter = !leaveOpen;
    }

    /// <summary>The path currently being written to — the temporary file while a run is in flight.</summary>
    public string? WritingTo => _writingTo;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_csv is not null)
        {
            return ValueTask.CompletedTask;
        }

        if (_targetPath is not null)
        {
            // The temporary file must sit in the target's own directory: a rename is only atomic
            // within a volume, and the system temp folder is often on a different one.
            _writingTo = _options.WriteAtomically
                ? $"{_targetPath}.{Guid.NewGuid():N}.tmp"
                : _targetPath;

            var file = _fileSystem.FileInfo.New(_writingTo);

            // Reads better than combining Path.GetDirectoryName with Directory.CreateDirectory, and
            // Create is a no-op when the directory already exists.
            file.Directory?.Create();

            _writer = new StreamWriter(file.Create(), _options.Encoding);
        }

        _csv = new CsvWriter(_writer!, _options.CreateConfiguration());
        _options.ConfigureContext?.Invoke(_csv.Context);

        if (_options.HasHeaderRecord)
        {
            _csv.WriteHeader<TRow>();
            _csv.NextRecord();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (_csv is null)
        {
            return Error.Failure(
                "csv.not_initialized",
                $"{nameof(CsvSink<TRow>)} has no open writer. It is opened during InitializeAsync, " +
                "which the pipeline calls before the first write.");
        }

        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Written straight through, so the pooled batch is never retained past this call.
            _csv.WriteRecord(batch.Span[i]);
            await _csv.NextRecordAsync().ConfigureAwait(false);
        }

        return batch.Length;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_csv is null)
        {
            return Result.Success;
        }

        await _csv.FlushAsync().ConfigureAwait(false);

        // Closed before the rename, not after: an open handle makes File.Move fail on Windows, and
        // renaming a file that has not finished flushing would promote a partial one anywhere.
        await CloseAsync().ConfigureAwait(false);

        if (_targetPath is not null && _options.WriteAtomically && _writingTo is not null)
        {
            _fileSystem.File.Move(_writingTo, _targetPath, overwrite: true);
            _writingTo = _targetPath;
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Never promotes. Disposal runs on the failure path too, so a temporary file reaching this
        // point without having been completed is exactly the one that must not become the target.
        await CloseAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseAsync()
    {
        if (_csv is not null)
        {
            await _csv.DisposeAsync().ConfigureAwait(false);
            _csv = null;
        }

        if (_ownsWriter && _writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        _writer = null;
    }
}
