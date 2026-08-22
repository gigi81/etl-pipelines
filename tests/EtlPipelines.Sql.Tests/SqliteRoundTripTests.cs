using System.Data;
using EtlPipelines.Core;

namespace EtlPipelines.Sql.Tests;

/// <summary>
/// Reading from and writing to a real database, through SQLite.
/// </summary>
/// <remarks>
/// SQLite carries the behavioural coverage for the SQL connectors because it needs no container and
/// so runs on every CI leg. The container-backed providers reuse these expectations against their own
/// engines; what is specific to them is the bulk-load path, not the contract.
/// </remarks>
public sealed class SqliteRoundTripTests : IAsyncDisposable
{
    private const string PipelineName = "orders";
    private const string Load = "load";
    private const string Extract = "extract";

    private readonly SqliteHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    public sealed record Order(long Id, string Customer, decimal Amount);

    private static Order Map(IDataRecord r) => new(r.GetInt64(0), r.GetString(1), r.GetDecimal(2));

    private static Order[] Sample(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new Order(i, $"customer-{i}", i * 1.5m))];

    private Task CreateTable() =>
        _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");

    [Test]
    public async Task Writes_rows_and_reads_them_back()
    {
        //arrange
        await CreateTable();
        var readBack = new CollectingSink<Order>();
        var original = Sample(3);

        _host.AddEtlPipeline(Load, b => b
                 .From(new ArraySource<Order>(original))
                 .ToSqlTable(_host.Open, "orders"))
             .AddEtlPipeline(Extract, b => b
                 .FromSql(_host.Open, "SELECT Id, Customer, Amount FROM orders ORDER BY Id", Map)
                 .To(_ => readBack));

        //act
        var load = await _host.RunAsync(Load);
        var extract = await _host.RunAsync(Extract);

        //assert
        load.IsError.Should().BeFalse(load.IsError ? load.FirstError.Description : null);
        extract.IsError.Should().BeFalse(extract.IsError ? extract.FirstError.Description : null);
        readBack.Rows.Should().Equal(original);
    }

    [Test]
    public async Task Streams_correctly_across_many_batch_boundaries()
    {
        //arrange
        // 5,000 rows against a 32-row batch is ~156 boundaries, where a source that mishandles a
        // partly filled buffer or a sink that drops the tail of a batch would show up.
        const int rows = 5_000;
        await CreateTable();
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Load, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .From(new ArraySource<Order>(Sample(rows)))
                 .ToSqlTable(_host.Open, "orders"))
             .AddEtlPipeline(Extract, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .FromSql(_host.Open, "SELECT Id, Customer, Amount FROM orders ORDER BY Id", Map)
                 .To(_ => readBack));

        //act
        var load = await _host.RunAsync(Load);
        var extract = await _host.RunAsync(Extract);

        //assert
        load.IsError.Should().BeFalse(load.IsError ? load.FirstError.Description : null);
        extract.IsError.Should().BeFalse(extract.IsError ? extract.FirstError.Description : null);

        load.Value.RowsWritten.Should().Be(rows);
        readBack.Rows.Should().HaveCount(rows);
        readBack.Rows.Select(r => r.Id).Should().Equal(Enumerable.Range(1, rows).Select(i => (long)i));
    }

    [Test]
    public async Task A_failed_run_leaves_the_table_as_it_was()
    {
        //arrange
        // The database counterpart of the file connectors' temp-file-and-rename. Without the
        // transaction a run that dies part-way would leave some fraction of the rows behind, which a
        // downstream job cannot tell from a finished load.
        await CreateTable();

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Order>(Sample(400)))
            .Branch(
                b1 => b1.ToSqlTable(_host.Open, "orders"),
                b2 => b2.To(new FailingSink<Order>(failAfter: 40))));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders"))
            .Should().Be(0, "an uncommitted transaction leaves nothing behind");
    }

    [Test]
    public async Task A_failed_run_does_not_disturb_rows_that_were_already_there()
    {
        //arrange
        await CreateTable();
        await _host.ExecuteAsync("INSERT INTO orders (Id, Customer, Amount) VALUES (999, 'previous', 1)");

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Order>(Sample(400)))
            .Branch(
                b1 => b1.ToSqlTable(_host.Open, "orders"),
                b2 => b2.To(new FailingSink<Order>(failAfter: 40))));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(1);
        (await _host.ScalarAsync<string>("SELECT Customer FROM orders")).Should().Be("previous");
    }

    [Test]
    public async Task Keeps_what_it_wrote_when_the_transaction_is_turned_off()
    {
        //arrange
        // The documented trade-off: without a transaction a partial load stays partial. Asserting it
        // keeps the option honest rather than implied.
        await CreateTable();

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Order>(Sample(400)))
            .Branch(
                b1 => b1.ToSqlTable(_host.Open, "orders", o => o.UseTransaction = false),
                // Delayed so the database branch provably writes before this gives up: this test is
                // about what survives a failure, which needs something to have been written first.
                b2 => b2.To(new FailingSink<Order>(failAfter: 40, TimeSpan.FromMilliseconds(10)))));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders"))
            .Should().BeGreaterThan(0, "nothing rolls back what was never in a transaction");
    }

    [Test]
    public async Task Writes_only_the_columns_it_was_asked_for()
    {
        //arrange
        // The identity-column case: the row type carries a value the database is meant to supply.
        await _host.ExecuteAsync(
            "CREATE TABLE orders (Id INTEGER PRIMARY KEY AUTOINCREMENT, Customer TEXT, Amount NUMERIC)");

        _host.AddEtlPipeline(Load, b => b
            .From(new ArraySource<Order>(Sample(3)))
            .ToSqlTable(_host.Open, "orders", o => o.Columns = ["Customer", "Amount"]));

        //act
        var load = await _host.RunAsync(Load);

        //assert
        load.IsError.Should().BeFalse(load.IsError ? load.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(3);
        (await _host.ScalarAsync<long>("SELECT MIN(Id) FROM orders"))
            .Should().Be(1, "the database assigned the key, not the row type");
    }

    [Test]
    public async Task Reads_the_query_from_the_start_on_every_run()
    {
        //arrange
        await CreateTable();
        await _host.ExecuteAsync("INSERT INTO orders (Id, Customer, Amount) VALUES (1, 'a', 1), (2, 'b', 2)");

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(Extract, b => b
            .FromSql(_host.Open, "SELECT Id, Customer, Amount FROM orders ORDER BY Id", Map)
            .To(_ => sink));

        //act
        var first = await _host.RunAsync(Extract);
        var second = await _host.RunAsync(Extract);

        //assert
        first.Value.RowsRead.Should().Be(2);
        second.Value.RowsRead.Should().Be(
            2,
            "the run scope builds its own source and opens its own connection, rather than resuming");
        sink.Rows.Should().HaveCount(4);
    }

    [Test]
    public async Task Binds_parameters_rather_than_pasting_them_into_the_sql()
    {
        //arrange
        await CreateTable();
        await _host.ExecuteAsync(
            "INSERT INTO orders (Id, Customer, Amount) VALUES (1, 'keep', 1), (2, 'drop', 2)");

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(Extract, b => b
            .FromSql(
                _host.Open,
                "SELECT Id, Customer, Amount FROM orders WHERE Customer = @name",
                Map,
                new SqlSourceOptions
                {
                    Configure = command =>
                    {
                        var p = command.CreateParameter();
                        p.ParameterName = "@name";
                        p.Value = "keep";
                        command.Parameters.Add(p);
                    },
                })
            .To(_ => sink));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().ContainSingle();
        sink.Rows[0].Customer.Should().Be("keep");
    }

    [Test]
    public async Task Reports_a_clear_error_when_written_to_before_initialization()
    {
        //arrange
        var sink = new SqlSink<Order>(_host.Open, new SqlSinkOptions { Table = "orders" });

        //act
        var written = await sink.WriteAsync(Sample(1), CancellationToken.None);

        //assert
        written.IsError.Should().BeTrue();
        written.FirstError.Code.Should().Be("sql.not_initialized");
    }
}
