using System.Data;
using System.Data.Common;
using System.Text;

namespace EtlPipelines.Extensions.Sql.Ports;

/// <summary>
/// Writes rows into a database table.
/// </summary>
/// <remarks>
/// <para>
/// By default the whole run is one transaction, committed from <see cref="CompleteAsync"/> — the hook
/// the runtime calls exactly once, and only when the run succeeded. That is the database counterpart
/// of the file connectors writing through a temporary file: a run that fails part-way leaves the
/// table as it was, rather than holding some fraction of the rows for a downstream job to read as if
/// the load had finished. It is also why <see cref="IAsyncCompletable"/> is separate from
/// <see cref="IAsyncDisposable"/>, which also runs on the failure path and rolls back here.
/// </para>
/// <para>
/// Rows go in through the provider's bulk loader when one is supplied, and otherwise through a
/// prepared parameterised INSERT reused for every row. The prepared statement is not the fastest way
/// to load a million rows, which is what <see cref="IBulkLoader"/> exists to fix, but it behaves
/// identically on every provider and needs no driver reference here.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to write.</typeparam>
public sealed class SqlSink<TRow> : IDataSink<TRow>, IAsyncInitializable, IAsyncCompletable
{
    private readonly Func<CancellationToken, ValueTask<DbConnection>> _open;
    private readonly SqlSinkOptions _options;
    private readonly IBulkLoader? _bulkLoader;

    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private DbCommand? _insert;
    private string[] _columns = [];
    private Func<TRow, object?>[] _readers = [];
    private bool _committed;

    /// <summary>Writes into the table named by <paramref name="options"/>.</summary>
    /// <param name="openConnection">Opens the connection. Called once per run.</param>
    /// <param name="options">The destination table, columns and transaction behaviour.</param>
    /// <param name="bulkLoader">The provider's fast path, when one is available.</param>
    public SqlSink(
        Func<CancellationToken, ValueTask<DbConnection>> openConnection,
        SqlSinkOptions options,
        IBulkLoader? bulkLoader = null)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Table);

        _open = openConnection;
        _options = options;
        _bulkLoader = bulkLoader;
    }

    /// <summary>Writes into the table named by <paramref name="options"/> over a named connection.</summary>
    /// <param name="connections">The factory for the connection this sink writes to.</param>
    /// <param name="options">The destination table, columns and transaction behaviour.</param>
    /// <param name="bulkLoader">The provider's fast path, when one is available.</param>
    public SqlSink(
        IDbConnectionFactory connections,
        SqlSinkOptions options,
        IBulkLoader? bulkLoader = null)
        : this(
            (connections ?? throw new ArgumentNullException(nameof(connections))).OpenAsync,
            options,
            bulkLoader)
    {
    }

    private bool UseBulkLoader => _bulkLoader is not null && _options.UseBulkLoader;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        (_columns, _readers) = RowAccessor<TRow>.For(_options.Columns);

        if (_columns.Length == 0)
        {
            throw new InvalidOperationException(
                $"{typeof(TRow).Name} has no readable properties matching the columns to write, so " +
                $"there is nothing to insert into '{_options.Table}'.");
        }

        _connection = await _open(cancellationToken).ConfigureAwait(false);

        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_options.UseTransaction)
        {
            _transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!UseBulkLoader)
        {
            _insert = CreateInsertCommand();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return Error.Failure(
                "sql.not_initialized",
                $"{nameof(SqlSink<TRow>)} has no open connection. It is opened during " +
                "InitializeAsync, which the pipeline calls before the first write.");
        }

        if (batch.Length == 0)
        {
            return 0;
        }

        try
        {
            return UseBulkLoader
                ? await BulkWriteAsync(batch, cancellationToken).ConfigureAwait(false)
                : await InsertAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            return Error.Failure("sql.write_failed", exception.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            _committed = true;
            return Result.Success;
        }

        try
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;
        }
        catch (DbException exception)
        {
            return Error.Failure("sql.commit_failed", exception.Message);
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Never commits. Disposal runs on the failure path too, so a transaction reaching this point
        // uncommitted is exactly the one whose rows must not be kept.
        if (_transaction is not null)
        {
            if (!_committed)
            {
                try
                {
                    await _transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Deliberately anything. A transaction reaching disposal uncommitted belongs to a
                    // run that has already failed, and the reasons it may now be unusable are not all
                    // DbException: cancelling a run mid-COPY leaves Npgsql's transaction disposed, so
                    // rolling back throws ObjectDisposedException. Either way the server discards an
                    // uncommitted transaction when the connection closes, so the rollback is a
                    // courtesy - and throwing here would replace the run's real error with this one.
                }
            }

            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }

        if (_insert is not null)
        {
            await _insert.DisposeAsync().ConfigureAwait(false);
            _insert = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    private async ValueTask<int> BulkWriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        using var rows = new BatchDataReader<TRow>(batch, _columns, _readers);

        return await _bulkLoader!
            .LoadAsync(_connection!, _transaction, _options.Table, _columns, rows, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<int> InsertAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        for (var i = 0; i < batch.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The row is read straight into the parameters, so the pooled batch is never retained
            // past this call.
            var row = batch.Span[i];
            for (var c = 0; c < _readers.Length; c++)
            {
                _insert!.Parameters[c].Value = _readers[c](row) ?? DBNull.Value;
            }

            await _insert!.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return batch.Length;
    }

    private DbCommand CreateInsertCommand()
    {
        var command = _connection!.CreateCommand();
        command.Transaction = _transaction;

        if (_options.CommandTimeout is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        var text = new StringBuilder()
            .Append("INSERT INTO ").Append(_options.Table).Append(" (")
            .AppendJoin(", ", _columns)
            .Append(") VALUES (");

        for (var i = 0; i < _columns.Length; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            // Named p0, p1 ... behind the provider's own marker; see SqlSinkOptions.ParameterPrefix
            // for why that is not simply "@".
            var name = $"{_options.ParameterPrefix}p{i}";
            text.Append(name);

            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{i}";
            command.Parameters.Add(parameter);
        }

        command.CommandText = text.Append(')').ToString();
        return command;
    }
}
