using System.IO.Abstractions;
using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Reads rows from a single JSON array holding every row.
/// </summary>
/// <remarks>
/// <para>
/// The stream is opened during <see cref="InitializeAsync"/> rather than at construction, so a
/// source registered in the container holds no file handle between runs. Combined with the scoped
/// registration the builder applies, each run opens the file itself and the run's scope closes it.
/// </para>
/// <para>
/// The file is an <see cref="IFileInfo"/> rather than a path, so the source never has to be told
/// which filesystem to use - the file already knows, through <see cref="IFileSystemInfo.FileSystem"/>.
/// That is also what lets the whole source run against an in-memory filesystem in a test.
/// </para>
/// <para>
/// Rows still arrive one at a time - <c>JsonSerializer.DeserializeAsyncEnumerable</c> streams the
/// array rather than buffering it whole - but there is no equivalent here of
/// <see cref="JsonLinesSource{TRow}.MalformedRows"/>: a malformed element leaves the underlying
/// reader's position inside the array unrecoverable, so <see cref="ReadAsync"/> fails the whole read
/// rather than skipping just that element. See <see cref="JsonLinesSource{TRow}"/> for the
/// counterpart that can skip a bad record, because a line - unlike an array element - is a recovery
/// boundary a parser can resynchronise on.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
public sealed class JsonArraySource<TRow> : IDataSource<TRow>, IAsyncInitializable
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly JsonArraySourceOptions _options;
    private readonly bool _ownsStream;

    private Stream? _stream;
    private IAsyncEnumerator<TRow?>? _rows;
    private bool _exhausted;

    /// <summary>Reads <paramref name="file"/>.</summary>
    /// <param name="file">The file to read. Its filesystem is the one the source reads through.</param>
    /// <param name="options">Format settings. Defaults are camelCase, invariant.</param>
    public JsonArraySource(IFileInfo file, JsonArraySourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);

        _options = options ?? new JsonArraySourceOptions();
        _ownsStream = true;
        _open = _ => new ValueTask<Stream>(file.OpenRead());
    }

    /// <summary>Reads a JSON array from a stream opened when the run starts.</summary>
    /// <param name="open">Opens the stream. Called once per run.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="leaveOpen">Set when the caller keeps ownership of the stream.</param>
    public JsonArraySource(
        Func<CancellationToken, ValueTask<Stream>> open,
        JsonArraySourceOptions? options = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
        _options = options ?? new JsonArraySourceOptions();
        _ownsStream = !leaveOpen;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        _stream = await _open(cancellationToken).ConfigureAwait(false);
        _rows = JsonSerializer
            .DeserializeAsyncEnumerable<TRow>(_stream, _options.SerializerOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_rows is null)
        {
            return Error.Failure(
                "json.not_initialized",
                $"{nameof(JsonArraySource<TRow>)} has no open stream. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first read.");
        }

        if (_exhausted)
        {
            return 0;
        }

        var count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool moved;
            try
            {
                moved = await _rows.MoveNextAsync().ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // Unlike JsonLinesSource, there is no line to resynchronise on: the underlying
                // reader's position inside the array is now unrecoverable, so the read fails
                // outright rather than skipping one element and continuing.
                return Error.Validation(
                    "json.malformed_array",
                    $"The JSON array could not be parsed: {ex.Message}");
            }

            if (!moved)
            {
                _exhausted = true;
                break;
            }

            buffer.Span[count++] = _rows.Current!;
        }

        return count;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_rows is not null)
        {
            await _rows.DisposeAsync().ConfigureAwait(false);
            _rows = null;
        }

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
