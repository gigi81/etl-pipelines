using System.Data;
using System.Data.Common;

namespace EtlPipelines.Sql;

/// <summary>
/// Reads rows from a SQL query.
/// </summary>
/// <remarks>
/// <para>
/// A thin arrangement over <see cref="DataReaderSource{TRow}"/>: it owns the connection and the
/// command, and hands the resulting cursor to the source that already knows how to page a reader into
/// batches. The connection is opened during <see cref="InitializeAsync"/> rather than at construction,
/// so a source registered in the container holds no connection between runs — each run opens its own
/// and the run's scope closes it.
/// </para>
/// <para>
/// The mapping delegate is not optional decoration; see <see cref="DataReaderSource{TRow}"/> for why
/// a batch built from the record itself would hold one object showing the last row read.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to read into.</typeparam>
public sealed class SqlSource<TRow> : IDataSource<TRow>, IAsyncInitializable
{
    private readonly Func<CancellationToken, ValueTask<DbConnection>> _open;
    private readonly string _sql;
    private readonly Func<IDataRecord, TRow> _map;
    private readonly SqlSourceOptions _options;

    private DbConnection? _connection;
    private DbCommand? _command;
    private DataReaderSource<TRow>? _rows;

    /// <summary>Reads the results of <paramref name="sql"/>.</summary>
    /// <param name="openConnection">Opens the connection. Called once per run.</param>
    /// <param name="sql">The query to run.</param>
    /// <param name="map">Copies the columns of the current row out into a row object.</param>
    /// <param name="options">Command settings and parameter binding.</param>
    public SqlSource(
        Func<CancellationToken, ValueTask<DbConnection>> openConnection,
        string sql,
        Func<IDataRecord, TRow> map,
        SqlSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(map);

        _open = openConnection;
        _sql = sql;
        _map = map;
        _options = options ?? new SqlSourceOptions();
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_rows is not null)
        {
            return;
        }

        _connection = await _open(cancellationToken).ConfigureAwait(false);

        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        _command = _connection.CreateCommand();
        _command.CommandText = _sql;
        _command.CommandType = _options.CommandType;

        if (_options.CommandTimeout is { } timeout)
        {
            _command.CommandTimeout = timeout;
        }

        _options.Configure?.Invoke(_command);

        var reader = await _command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // leaveOpen: this source owns the reader's lifetime through the command and connection it
        // opened, and disposes all three together below.
        _rows = new DataReaderSource<TRow>(reader, _map, leaveOpen: false);
        await _rows.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_rows is null)
        {
            return ValueTask.FromResult<ErrorOr<int>>(Error.Failure(
                "sql.not_initialized",
                $"{nameof(SqlSource<TRow>)} has no open reader. It is opened during InitializeAsync, " +
                "which the pipeline calls before the first read."));
        }

        return _rows.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_rows is not null)
        {
            await _rows.DisposeAsync().ConfigureAwait(false);
            _rows = null;
        }

        if (_command is not null)
        {
            await _command.DisposeAsync().ConfigureAwait(false);
            _command = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}
