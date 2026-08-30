using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Json;

/// <summary>
/// Reads rows from a single JSON array holding every row - the array at the document's root by
/// default, or nested under <see cref="JsonArraySourceOptions.Path"/>.
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
/// This type itself does no JSON parsing: root and nested arrays are read in genuinely different
/// ways, so each has its own <see cref="IJsonArrayReader{TRow}"/> implementation -
/// <see cref="RootJsonArrayReader{TRow}"/> and <see cref="NestedJsonArrayReader{TRow}"/> - and this
/// type is only the seam between the port contract and whichever of the two applies, picked once in
/// <see cref="InitializeAsync"/> from whether <see cref="JsonArraySourceOptions.Path"/> is empty.
/// </para>
/// <para>
/// There is no equivalent here of <see cref="JsonLinesSource{TRow}.MalformedRows"/>: a malformed
/// element leaves the underlying reader's position inside the array unrecoverable, so
/// <see cref="ReadAsync"/> fails the whole read rather than skipping just that element. See
/// <see cref="JsonLinesSource{TRow}"/> for the counterpart that can skip a bad record, because a line
/// - unlike an array element - is a recovery boundary a parser can resynchronise on.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
public sealed class JsonArraySource<TRow> : IDataSource<TRow>, IAsyncInitializable
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;
    private readonly JsonArraySourceOptions _options;
    private readonly bool _ownsStream;

    private Stream? _stream;
    private IJsonArrayReader<TRow>? _reader;

    /// <summary>Reads <paramref name="file"/>.</summary>
    /// <param name="file">The file to read. Its filesystem is the one the source reads through.</param>
    /// <param name="options">Format settings. Defaults are camelCase, invariant, the document's root.</param>
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

        _reader = _options.Path.Count == 0
            ? new RootJsonArrayReader<TRow>(_stream, _options.SerializerOptions, cancellationToken)
            : await NestedJsonArrayReader<TRow>
                .OpenAsync(_stream, _options.Path, _options.SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            return Error.Failure(
                "json.not_initialized",
                $"{nameof(JsonArraySource<TRow>)} has no open stream. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first read.");
        }

        return await _reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
            _reader = null;
        }

        if (_ownsStream && _stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _stream = null;
    }
}
