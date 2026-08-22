using System.Data.Common;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Sql.Tests;

/// <summary>
/// A container plus a private SQLite database, wired the way an application would wire them.
/// </summary>
/// <remarks>
/// The database is a file in a scratch directory rather than <c>:memory:</c>, because an in-memory
/// SQLite database lives only as long as the connection that opened it — and these ports deliberately
/// open a new connection per run, which is one of the things worth testing.
/// </remarks>
public sealed class SqliteHost : IAsyncDisposable
{
    private readonly ServiceCollection _services = [];
    private readonly string _directory;
    private ServiceProvider? _provider;

    public SqliteHost()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"etl-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        ConnectionString = $"Data Source={Path.Combine(_directory, "test.db")}";
        Open = SqliteConnections.Open(ConnectionString);
    }

    public string ConnectionString { get; }

    public Func<CancellationToken, ValueTask<DbConnection>> Open { get; }

    public SqliteHost Configure(Action<IServiceCollection> configure)
    {
        ThrowIfBuilt();
        configure(_services);
        return this;
    }

    public SqliteHost AddEtlPipeline(string name, Action<IPipelineBuilder> build)
    {
        ThrowIfBuilt();
        _services.AddEtlPipeline(name, build);
        return this;
    }

    public IServiceProvider Services => _provider ??= _services.BuildServiceProvider();

    public Task<ErrorOr<PipelineResult>> RunAsync(string name, CancellationToken cancellationToken = default) =>
        Services.GetRequiredEtlPipeline(name).RunAsync(cancellationToken);

    /// <summary>Runs a statement outside any pipeline — creating the destination table, usually.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Reads one scalar back, for asserting on what a run actually left behind.</summary>
    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        // SQLite keeps the file handle in a pool; without this the directory will not delete on Windows.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test over.
        }
    }

    private void ThrowIfBuilt()
    {
        if (_provider is not null)
        {
            throw new InvalidOperationException(
                "The container has already been built. Register everything before the first run, " +
                "because AddEtlPipeline registers a pipeline's components as it composes them.");
        }
    }
}

/// <summary>Feeds a fixed array of rows into a pipeline.</summary>
public sealed class ArraySource<TRow>(IReadOnlyList<TRow> rows) : IDataSource<TRow>
{
    private int _position;

    public ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        var count = Math.Min(buffer.Length, rows.Count - _position);
        if (count <= 0)
        {
            return ValueTask.FromResult<ErrorOr<int>>(0);
        }

        for (var i = 0; i < count; i++)
        {
            buffer.Span[i] = rows[_position + i];
        }

        _position += count;
        return ValueTask.FromResult<ErrorOr<int>>(count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Collects whatever a pipeline writes to it.</summary>
public sealed class CollectingSink<TRow> : IDataSink<TRow>
{
    private readonly List<TRow> _rows = [];

    public IReadOnlyList<TRow> Rows => _rows;

    public ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        _rows.AddRange(batch.ToArray());
        return ValueTask.FromResult<ErrorOr<int>>(batch.Length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Fails once it has seen a given number of rows, to exercise mid-run failure.</summary>
public sealed class FailingSink<TRow>(int failAfter) : IDataSink<TRow>
{
    private int _seen;

    public ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        _seen += batch.Length;
        return ValueTask.FromResult<ErrorOr<int>>(_seen >= failAfter
            ? Error.Failure("sink.exploded", "Deliberate failure.")
            : batch.Length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
