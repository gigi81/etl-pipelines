using System.IO.Abstractions;
using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Writes rows to a single JSON array holding every row.
/// </summary>
/// <remarks>
/// <para>
/// By default the rows stream into a temporary file beside the target, which is renamed into place
/// from <see cref="CompleteAsync"/> - the hook the runtime calls exactly once, and only when the run
/// succeeded. The target path therefore either does not exist or holds a whole file - never one
/// missing its closing <c>]</c>, which a downstream reader would otherwise have to notice for itself.
/// A failed run leaves the temporary file behind for inspection, unterminated array and all.
/// </para>
/// <para>
/// This is the reason <see cref="IAsyncCompletable"/> exists separately from
/// <see cref="IAsyncDisposable"/>: disposal also runs on the failure path, where promoting a
/// half-written file is precisely the wrong thing to do.
/// </para>
/// <para>
/// The destination is an <see cref="IFileInfo"/> rather than a path, so the sink never has to be told
/// which filesystem to use - the file already knows, through <see cref="IFileSystemInfo.FileSystem"/>.
/// That is also what lets the whole sink run against an in-memory filesystem in a test.
/// </para>
/// <para>
/// See <see cref="JsonLinesSink{TRow}"/> for the counterpart that writes JSON Lines instead.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to write.</typeparam>
public sealed class JsonArraySink<TRow> : IDataSink<TRow>, IAsyncInitializable, IAsyncCompletable
{
    private readonly IFileInfo? _target;
    private readonly JsonSinkOptions _options;
    private readonly bool _ownsStream;

    private Stream? _stream;
    private Utf8JsonWriter? _writer;
    private IFileInfo? _writingTo;

    /// <summary>Writes to <paramref name="file"/>, overwriting it if it already exists.</summary>
    /// <param name="file">Destination file. Its filesystem is the one the sink writes through.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    public JsonArraySink(IFileInfo file, JsonSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        _target = file;
        _options = options ?? new JsonSinkOptions();
        _ownsStream = true;
    }

    /// <summary>
    /// Writes to an existing <see cref="Stream"/>.
    /// </summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the stream.</param>
    /// <remarks>
    /// There is no file to rename here, so the atomic behaviour does not apply: rows reach the stream
    /// as they are produced, and completion only writes the closing <c>]</c> and flushes. A caller
    /// who needs all-or-nothing must arrange it themselves, or use the file-based constructor.
    /// </remarks>
    public JsonArraySink(Stream stream, JsonSinkOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _options = options ?? new JsonSinkOptions();
        _ownsStream = !leaveOpen;
    }

    /// <summary>
    /// The file currently being written - the temporary one while a run is in flight, and the target
    /// once it has been promoted.
    /// </summary>
    public IFileInfo? WritingTo => _writingTo;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            return ValueTask.CompletedTask;
        }

        if (_target is not null)
        {
            // The temporary file is a sibling of the target on purpose: a rename is only atomic
            // within a volume, and the system temp directory is often on a different one.
            _writingTo = _options.WriteAtomically
                ? _target.Directory.File($"{Guid.NewGuid():N}.tmp")
                : _target;

            // Reads better than pulling the directory name out of the path by hand, and Create is a
            // no-op when the directory is already there.
            _writingTo.Directory?.Create();

            _stream = _writingTo.Create();
        }

        // Utf8JsonWriter never owns or disposes the stream it is given, so there is no leaveOpen
        // concept to reconcile here the way JsonLinesSink has to for its StreamWriter - CloseAsync
        // below is already the only place _stream itself gets disposed.
        _writer = new Utf8JsonWriter(_stream!, new JsonWriterOptions { Indented = _options.SerializerOptions.WriteIndented });
        _writer.WriteStartArray();

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            return ValueTask.FromResult<ErrorOr<int>>(Error.Failure(
                "json.not_initialized",
                $"{nameof(JsonArraySink<TRow>)} has no open writer. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first write."));
        }

        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Synchronous: Utf8JsonWriter has no async write, only an async flush. It buffers
            // internally and flushes itself once that buffer fills, so this does not block on I/O for
            // every row - only occasionally, same as the buffered StreamWriter on the Lines side.
            JsonSerializer.Serialize(_writer, batch.Span[i], _options.SerializerOptions);
        }

        return ValueTask.FromResult<ErrorOr<int>>(batch.Length);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            return Result.Success;
        }

        _writer.WriteEndArray();
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Closed before the rename, not after: an open handle makes the move fail on Windows, and
        // renaming a file that has not finished flushing would promote a partial one anywhere.
        await CloseAsync().ConfigureAwait(false);

        if (_target is not null && _options.WriteAtomically && _writingTo is not null)
        {
            _writingTo.MoveTo(_target.FullName, overwrite: true);
            _writingTo = _target;
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Never promotes, and never writes the closing "]": disposal runs on the failure path too,
        // and an array left unterminated here is the honest shape of a run that did not finish, not
        // something to paper over on the way out.
        await CloseAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseAsync()
    {
        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _writer = null;
        }

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
