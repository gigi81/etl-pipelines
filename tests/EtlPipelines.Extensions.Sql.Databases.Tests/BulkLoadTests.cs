using System.Data;
using System.Data.Common;
using EtlPipelines.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Extensions.Sql.Databases.Tests;

/// <summary>
/// What every provider package has to get right, run against its real engine.
/// </summary>
/// <remarks>
/// The contract itself is covered by the SQLite suite, which needs no container and runs on every CI
/// leg. What cannot be covered there is the part that differs per provider: the bulk-load path, and
/// whether a bulk load still rolls back with the transaction the sink opened. Those are what these
/// tests exist for, so they are deliberately few.
/// </remarks>
[Category("Docker")]
public abstract class BulkLoadTests<TFixture>
    where TFixture : IDatabaseFixture
{
    private const string Load = "load";
    private const string Extract = "extract";

    protected BulkLoadTests(TFixture fixture) => Fixture = fixture;

    protected TFixture Fixture { get; }

    /// <summary>Opens a connection to the container's database.</summary>
    protected abstract Func<CancellationToken, ValueTask<DbConnection>> Open();

    /// <summary>The provider's fast path.</summary>
    protected abstract IBulkLoader BulkLoader { get; }

    /// <summary>DDL for the destination table, which every engine spells differently.</summary>
    protected abstract string CreateTableSql(string table);

    /// <summary>Oracle binds with a colon; everyone else here uses an at sign.</summary>
    protected virtual string ParameterPrefix => "@";

    /// <summary>A unique table per test, so tests sharing one container cannot collide.</summary>
    protected static string NewTableName() => $"orders_{Guid.NewGuid():N}"[..24];

    public sealed record Order(int Id, string Customer, decimal Amount);

    private static Order[] Sample(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new Order(i, $"customer-{i}", i * 1.5m))];

    private static Order Map(IDataRecord r) =>
        new(Convert.ToInt32(r.GetValue(0)), r.GetString(1), Convert.ToDecimal(r.GetValue(2)));

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await Open()(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = await Open()(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private ServiceProvider BuildHost(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(BulkLoader);
        configure(services);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Bulk_loads_rows_and_reads_them_back()
    {
        //arrange
        var table = NewTableName();
        await ExecuteAsync(CreateTableSql(table));
        var readBack = new CollectingSink<Order>();
        var original = Sample(500);

        var provider = BuildHost(services => services
            .AddEtlPipeline(Load, b => b
                .WithOptions(o => o.BatchSize = 100)
                .From(new ArraySource<Order>(original))
                .ToSqlTable(Open(), table))
            .AddEtlPipeline(Extract, b => b
                .FromSql(Open(), $"SELECT Id, Customer, Amount FROM {table} ORDER BY Id", Map)
                .To(_ => readBack)));

        //act
        var load = await provider.GetRequiredEtlPipeline(Load).RunAsync(CancellationToken.None);
        var extract = await provider.GetRequiredEtlPipeline(Extract).RunAsync(CancellationToken.None);

        //assert
        load.IsError.Should().BeFalse(load.IsError ? load.FirstError.Description : null);
        extract.IsError.Should().BeFalse(extract.IsError ? extract.FirstError.Description : null);

        load.Value.RowsWritten.Should().Be(500);
        readBack.Rows.Should().HaveCount(500);
        readBack.Rows.Select(r => r.Id).Should().Equal(Enumerable.Range(1, 500));
        readBack.Rows[0].Customer.Should().Be("customer-1");
    }

    [Test]
    public async Task A_failed_run_rolls_the_bulk_load_back()
    {
        //arrange
        // The claim worth testing per provider: a bulk loader that manages its own transaction, or
        // commits as it goes, would leave rows behind here even though the run failed.
        var table = NewTableName();
        await ExecuteAsync(CreateTableSql(table));

        var provider = BuildHost(services => services
            .AddEtlPipeline(Load, b => b
                .WithOptions(o => o.BatchSize = 50)
                .From(new ArraySource<Order>(Sample(1_000)))
                .Branch(
                    b1 => b1.ToSqlTable(Open(), table),
                    b2 => b2.To(new FailingSink<Order>(failAfter: 200)))));

        //act
        var result = await provider.GetRequiredEtlPipeline(Load).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        (await CountAsync(table)).Should().Be(0, "an uncommitted transaction must leave nothing behind");
    }

    [Test]
    public async Task Falls_back_to_parameterised_inserts_when_the_bulk_path_is_turned_off()
    {
        //arrange
        // This is also what proves the parameter marker is right for this provider — a wrong one
        // binds nothing and the insert fails.
        var table = NewTableName();
        await ExecuteAsync(CreateTableSql(table));

        var provider = BuildHost(services => services
            .AddEtlPipeline(Load, b => b
                .From(new ArraySource<Order>(Sample(25)))
                .ToSqlTable(Open(), table, o =>
                {
                    o.UseBulkLoader = false;
                    o.ParameterPrefix = ParameterPrefix;
                })));

        //act
        var result = await provider.GetRequiredEtlPipeline(Load).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await CountAsync(table)).Should().Be(25);
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

    public async ValueTask<ErrorOr<int>> WriteAsync(
        ReadOnlyMemory<TRow> batch,
        CancellationToken cancellationToken)
    {
        // A short delay so the sibling branch provably reaches the database before this gives up;
        // without it "the run failed part-way" is not true in the sense the test cares about.
        await Task.Delay(5, cancellationToken);

        _seen += batch.Length;
        return _seen >= failAfter
            ? Error.Failure("sink.exploded", "Deliberate failure.")
            : batch.Length;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
