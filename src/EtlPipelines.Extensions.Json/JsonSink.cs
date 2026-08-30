using System.IO.Abstractions;
using System.Text;
using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Writes rows to a JSON file, either JSON Lines or a single JSON array. See
/// <see cref="JsonOptions.Format"/>.
/// </summary>
/// <remarks>
/// <para>
/// By default the rows stream into a temporary file beside the target, which is renamed into place
/// from <see cref="CompleteAsync"/> - the hook the runtime calls exactly once, and only when the run
/// succeeded. The target path therefore either does not exist or holds a whole file; a downstream
/// job can never pick up a truncated one, or, under <see cref="JsonFormat.Array"/>, one missing its
/// closing <c>]</c>. A failed run leaves the temporary file behind for inspection.
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
/// </remarks>
/// <typeparam name="TRow">The row type to write.</typeparam>
public sealed class JsonSink<TRow> : IDataSink<TRow>, IAsyncInitializable, IAsyncCompletable
{
    private readonly IFileInfo? _target;
    private readonly JsonSinkOptions _options;
    private readonly bool _ownsStream;

    private Stream? _stream;
    private StreamWriter? _lines;
    private Utf8JsonWriter? _array;
    private IFileInfo? _writingTo;

    /// <summary>Writes to <paramref name="file"/>, overwriting it if it already exists.</summary>
    /// <param name="file">Destination file. Its filesystem is the one the sink writes through.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    public JsonSink(IFileInfo file, JsonSinkOptions? options = null)
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
    /// as they are produced, and completion only flushes. A caller who needs all-or-nothing must
    /// arrange it themselves, or use the file-based constructor.
    /// </remarks>
    public JsonSink(Stream stream, JsonSinkOptions? options = null, bool leaveOpen = false)
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
        if (_lines is not null || _array is not null)
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

        if (_options.Format == JsonFormat.Array)
        {
            _array = new Utf8JsonWriter(_stream!, new JsonWriterOptions { Indented = _options.SerializerOptions.WriteIndented });
            _array.WriteStartArray();
        }
        else
        {
            // UTF-8 without a byte order mark, and "\n" rather than Environment.NewLine, so the file
            // is the same NDJSON on every platform it is written or read on.
            //
            // leaveOpen: true regardless of who owns _stream - CloseAsync below is the single place
            // that decides whether _stream itself gets disposed, based on _ownsStream. Without this,
            // disposing _lines would always close _stream out from under a caller who passed
            // leaveOpen: true to the Stream-based constructor.
            _lines = new StreamWriter(
                _stream!,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: -1,
                leaveOpen: true)
            {
                NewLine = "\n",
            };
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (_lines is null && _array is null)
        {
            return Error.Failure(
                "json.not_initialized",
                $"{nameof(JsonSink<TRow>)} has no open writer. It is opened during InitializeAsync, " +
                "which the pipeline calls before the first write.");
        }

        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_array is not null)
            {
                // Synchronous: Utf8JsonWriter has no async write, only an async flush. It buffers
                // internally and flushes itself once that buffer fills, so this does not block on I/O
                // for every row - only occasionally, same as the buffered StreamWriter on the Lines
                // side.
                JsonSerializer.Serialize(_array, batch.Span[i], _options.SerializerOptions);
            }
            else
            {
                var line = JsonSerializer.Serialize(batch.Span[i], _options.SerializerOptions);
                await _lines!.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        return batch.Length;
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_lines is null && _array is null)
        {
            return Result.Success;
        }

        if (_array is not null)
        {
            _array.WriteEndArray();
            await _array.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _lines!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

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
        // Never promotes. Disposal runs on the failure path too, so a temporary file reaching this
        // point without having been completed is exactly the one that must not become the target.
        // Also never writes the closing "]": an array left unterminated here is the honest shape of a
        // run that did not finish, not something to paper over on the way out.
        await CloseAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseAsync()
    {
        if (_array is not null)
        {
            await _array.DisposeAsync().ConfigureAwait(false);
            _array = null;
        }

        if (_lines is not null)
        {
            await _lines.DisposeAsync().ConfigureAwait(false);
            _lines = null;
        }

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
