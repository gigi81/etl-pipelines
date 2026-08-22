using System.Data;
using System.Data.Common;
using Dapper;

namespace EtlPipelines.Sql;

/// <summary>
/// An <see cref="IDataSource{TRow}"/> that pulls rows from an ADO.NET <see cref="IDataReader"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the bridge from any ADO.NET provider — SQL Server, PostgreSQL, MySQL, Oracle — into a
/// pipeline, since every one of them hands back an <see cref="IDataReader"/>.
/// </para>
/// <para>
/// <b>Rows must be materialised by the mapping delegate.</b> An <see cref="IDataRecord"/> is a cursor
/// positioned on the current row, not a value: it is the same object on every iteration, and its
/// contents change the moment the reader advances. Handing the record itself downstream would fill
/// the batch with N references to one object showing the last row read. The delegate's job is to copy
/// the columns out into a real row, and the signature enforces that by returning
/// <typeparamref name="TRow"/> rather than exposing the record.
/// </para>
/// <para>
/// The reader is opened during <see cref="InitializeAsync"/> rather than at construction, so a source
/// registered in the container does not hold an open cursor between runs. Combined with scoped
/// registration, each run opens its own reader and the run's scope closes it.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type this source produces.</typeparam>
public sealed class DataReaderSource<TRow> : IDataSource<TRow>, IAsyncInitializable
{
    private readonly Func<CancellationToken, ValueTask<IDataReader>>? _open;
    private readonly bool _ownsReader;
    private readonly bool _ownsMap;

    private Func<IDataReader, TRow>? _map;
    private IDataReader? _reader;
    private bool _exhausted;

    /// <summary>
    /// Creates a source that opens its reader when the run starts.
    /// </summary>
    /// <param name="open">
    /// Opens the reader. Called once per run, so this is where the command should be executed —
    /// typically <c>await command.ExecuteReaderAsync(ct)</c>.
    /// </param>
    /// <param name="map">
    /// Copies the current row out of the reader into a <typeparamref name="TRow"/>. Left
    /// <see langword="null"/>, Dapper's compiled parser for the row type is used.
    /// </param>
    public DataReaderSource(
        Func<CancellationToken, ValueTask<IDataReader>> open,
        Func<IDataReader, TRow>? map = null)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
        _map = map;
        _ownsMap = map is null;
        _ownsReader = true;
    }

    /// <summary>
    /// Creates a source over a reader that is already open.
    /// </summary>
    /// <param name="reader">The open reader to consume.</param>
    /// <param name="map">
    /// Copies the current row out of the reader into a <typeparamref name="TRow"/>. Left
    /// <see langword="null"/>, Dapper's compiled parser for the row type is used.
    /// </param>
    /// <param name="leaveOpen">
    /// Set when the caller keeps ownership of the reader. Otherwise the source disposes it, together
    /// with the run's scope.
    /// </param>
    /// <remarks>
    /// A reader is a one-shot forward cursor, so a source built this way cannot be run twice. Prefer
    /// the factory constructor for anything registered in a container.
    /// </remarks>
    public DataReaderSource(IDataReader reader, Func<IDataReader, TRow>? map = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
        _map = map;
        _ownsMap = map is null;
        _ownsReader = !leaveOpen;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_reader is null && _open is not null)
        {
            _reader = await _open(cancellationToken).ConfigureAwait(false);
        }

        // Built here rather than in the constructor: Dapper compiles the parser against the columns
        // the query actually returned, which nothing knows until the reader is open.
        _map ??= _reader?.GetRowParser<TRow>();
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_reader is null || _map is null)
        {
            return Error.Failure(
                "datareader.not_initialized",
                $"{nameof(DataReaderSource<TRow>)} has no reader. It is opened during InitializeAsync, " +
                "which the pipeline calls before the first read.");
        }

        if (_exhausted)
        {
            return 0;
        }

        var count = 0;
        while (count < buffer.Length)
        {
            if (!await AdvanceAsync(_reader, cancellationToken).ConfigureAwait(false))
            {
                // Latched, so a later call cannot walk off the end of a spent cursor.
                _exhausted = true;
                break;
            }

            // buffer.Span is re-acquired per row on purpose: a Span cannot live across an await.
            buffer.Span[count++] = Materialise(_reader);
        }

        return count;
    }

    /// <summary>
    /// Turns the row the reader is sitting on into a <typeparamref name="TRow"/>, rebuilding the
    /// mapping if this row does not have the shape the mapping was built for.
    /// </summary>
    /// <remarks>
    /// Only the mapping this class built for itself is ever rebuilt; one the caller supplied is
    /// theirs, and a failure in it is theirs to see.
    /// <para>
    /// The rebuild exists for SQLite, which types a <em>value</em> rather than a column: in a NUMERIC
    /// column, 4.5 comes back as a Double and 3.0 comes back as an Int64, because NUMERIC affinity
    /// stores a whole number as an integer. A parser compiled against one of those cannot read the
    /// other, and which one it was compiled against depends on nothing more than which rows the query
    /// happened to return. Rebuilding against the row in hand is the only thing that reads both.
    /// </para>
    /// <para>
    /// Driven by the failure rather than by checking every row, so a provider that reports stable
    /// column types — which is all four of the others — pays nothing at all. A SQLite column that
    /// genuinely alternates costs one rebuild per change; Dapper caches compiled parsers, so it is
    /// the throw and not the compile that is being paid for. Pass a mapping delegate to avoid it.
    /// </para>
    /// </remarks>
    private TRow Materialise(IDataReader reader)
    {
        try
        {
            return _map!(reader);
        }
        catch (DataException) when (_ownsMap)
        {
            _map = reader.GetRowParser<TRow>();
            return _map(reader);
        }
    }

    /// <summary>
    /// Advances the cursor, asynchronously when the provider supports it.
    /// </summary>
    /// <remarks>
    /// <see cref="IDataReader.Read"/> is synchronous and would block a thread-pool thread for the
    /// duration of a network round trip — the opposite of what a pipeline built on back-pressure
    /// wants. Every real provider's reader derives from <see cref="DbDataReader"/>, so the async path
    /// is what actually runs; the <see cref="IDataReader"/> fallback keeps the source usable with
    /// in-memory and legacy readers that offer nothing better.
    /// </remarks>
    private static ValueTask<bool> AdvanceAsync(IDataReader reader, CancellationToken cancellationToken) =>
        reader is DbDataReader asyncReader
            ? new ValueTask<bool>(asyncReader.ReadAsync(cancellationToken))
            : new ValueTask<bool>(reader.Read());

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_ownsReader)
        {
            _reader = null;
            return;
        }

        switch (_reader)
        {
            case DbDataReader asyncReader:
                await asyncReader.DisposeAsync().ConfigureAwait(false);
                break;
            case not null:
                _reader.Dispose();
                break;
        }

        _reader = null;
    }
}
