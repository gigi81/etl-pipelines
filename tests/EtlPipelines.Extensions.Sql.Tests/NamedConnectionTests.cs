using System.Data.Common;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Sql.Tests;

/// <summary>
/// Naming a database once and referring to it by name after that.
/// </summary>
/// <remarks>
/// The behaviour being pinned down is that a connection is resolved from the container per run rather
/// than captured when the pipeline was composed, that its connection string comes from the same
/// <c>ConnectionStrings</c> section an application would use, and that two named connections stay
/// apart — including their bulk loaders, which used to be a single unkeyed registration that the
/// second engine silently lost.
/// </remarks>
public sealed class NamedConnectionTests : IAsyncDisposable
{
    private const string Load = "load";
    private const string Extract = "extract";

    private readonly SqliteHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>Settable rather than positional, so the automatic mapping can materialise it.</summary>
    public sealed record Order
    {
        public long Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    private static Order[] Sample(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => new Order
        {
            Id = i,
            Customer = $"customer-{i}",
            Amount = i * 1.5m,
        }),
    ];

    private static IConfiguration Configuration(params (string Name, string Value)[] connectionStrings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(connectionStrings.Select(entry =>
                new KeyValuePair<string, string?>($"ConnectionStrings:{entry.Name}", entry.Value)))
            .Build();

    [Test]
    public async Task Takes_the_connection_string_from_configuration()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");

        _host.Configure(services => services
                 .AddSingleton(Configuration(("orders", _host.ConnectionString)))
                 .AddSqliteConnection("orders"))
             .AddEtlPipeline(Load, b => b
                 .From(new ArraySource<Order>(Sample(3)))
                 .ToSqlTable("orders", "orders"));

        //act
        var result = await _host.RunAsync(Load);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(3);
    }

    [Test]
    public async Task Takes_a_connection_string_given_directly()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");

        // No IConfiguration registered at all: the overload that is given the string does not need one.
        _host.Configure(services => services.AddSqliteConnection("orders", _host.ConnectionString))
             .AddEtlPipeline(Load, b => b
                 .From(new ArraySource<Order>(Sample(2)))
                 .ToSqlTable("orders", "orders"));

        //act
        var result = await _host.RunAsync(Load);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(2);
    }

    [Test]
    public async Task Reads_and_writes_over_the_same_named_connection()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");
        var readBack = new CollectingSink<Order>();
        var original = Sample(4);

        _host.Configure(services => services
                 .AddSingleton(Configuration(("orders", _host.ConnectionString)))
                 .AddSqliteConnection("orders"))
             .AddEtlPipeline(Load, b => b
                 .From(new ArraySource<Order>(original))
                 .ToSqlTable("orders", "orders"))
             .AddEtlPipeline(Extract, b => b
                 .FromSql<Order>("orders", "SELECT Id, Customer, Amount FROM orders ORDER BY Id")
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
    public async Task Keeps_each_named_connection_to_its_own_bulk_loader()
    {
        //arrange
        // The bug this guards against: one unkeyed IBulkLoader registration meant a pipeline writing
        // to two engines got whichever package registered first, for both ends.
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");
        var first = new RecordingBulkLoader();
        var second = new RecordingBulkLoader();

        _host.Configure(services => services
                 .AddSqliteConnection("first", _host.ConnectionString)
                 .AddSqliteConnection("second", _host.ConnectionString)
                 .AddKeyedSingleton<IBulkLoader>("first", first)
                 .AddKeyedSingleton<IBulkLoader>("second", second))
             .AddEtlPipeline("to-first", b => b
                 .From(new ArraySource<Order>(Sample(3)))
                 .ToSqlTable("first", "orders"))
             .AddEtlPipeline("to-second", b => b
                 .From(new ArraySource<Order>(Sample(5)))
                 .ToSqlTable("second", "orders"));

        //act
        var toFirst = await _host.RunAsync("to-first");
        var toSecond = await _host.RunAsync("to-second");

        //assert
        toFirst.IsError.Should().BeFalse(toFirst.IsError ? toFirst.FirstError.Description : null);
        toSecond.IsError.Should().BeFalse(toSecond.IsError ? toSecond.FirstError.Description : null);
        first.RowsSeen.Should().Be(3, "the first pipeline's loader saw only the first pipeline's rows");
        second.RowsSeen.Should().Be(5);
    }

    [Test]
    public async Task Says_what_to_do_when_the_connection_is_not_registered()
    {
        //arrange
        _host.AddEtlPipeline(Load, b => b
            .From(new ArraySource<Order>(Sample(1)))
            .ToSqlTable("nowhere", "orders"));

        //act
        var result = await _host.RunAsync(Load);

        //assert
        // The runtime turns a component that will not build into a failed run rather than letting the
        // exception out, so the message has to survive into the error to be worth writing.
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should()
            .Contain("No database connection named 'nowhere' is registered").And
            .Contain("AddSqliteConnection(\"nowhere\")").And
            .Contain("ConnectionStrings:nowhere");
    }

    [Test]
    public async Task Says_what_to_do_when_the_connection_string_is_missing_from_configuration()
    {
        //arrange
        _host.Configure(services => services
                 .AddSingleton(Configuration(("something-else", _host.ConnectionString)))
                 .AddSqliteConnection("orders"))
             .AddEtlPipeline(Load, b => b
                 .From(new ArraySource<Order>(Sample(1)))
                 .ToSqlTable("orders", "orders"));

        //act
        var result = await _host.RunAsync(Load);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should()
            .Contain("No connection string named 'orders'").And
            .Contain("ConnectionStrings:orders");
    }

    [Test]
    public async Task Says_what_to_do_when_configuration_is_not_registered_at_all()
    {
        //arrange
        _host.Configure(services => services.AddSqliteConnection("orders"))
             .AddEtlPipeline(Load, b => b
                 .From(new ArraySource<Order>(Sample(1)))
                 .ToSqlTable("orders", "orders"));

        //act
        var result = await _host.RunAsync(Load);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("no IConfiguration is registered");
    }

    [Test]
    public async Task Opens_a_new_connection_for_every_run()
    {
        //arrange
        // A connection captured when the pipeline was composed would be closed by the first run's
        // scope, and the second run would fail on it.
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");

        _host.Configure(services => services.AddSqliteConnection("orders", _host.ConnectionString))
             .AddEtlPipeline(Load, b => b
                 // A factory, not an instance: a source registered as an instance keeps its read
                 // position, and the second run would find it already exhausted.
                 .From(_ => new ArraySource<Order>(Sample(2)))
                 .ToSqlTable("orders", "orders"));

        //act
        var first = await _host.RunAsync(Load);
        var second = await _host.RunAsync(Load);

        //assert
        first.IsError.Should().BeFalse(first.IsError ? first.FirstError.Description : null);
        second.IsError.Should().BeFalse(second.IsError ? second.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(4);
    }

    /// <summary>Counts the rows handed to it, without writing them anywhere.</summary>
    private sealed class RecordingBulkLoader : IBulkLoader
    {
        public int RowsSeen { get; private set; }

        public async ValueTask<int> LoadAsync(
            DbConnection connection,
            DbTransaction? transaction,
            string table,
            IReadOnlyList<string> columns,
            DbDataReader rows,
            CancellationToken cancellationToken)
        {
            var count = 0;
            while (await rows.ReadAsync(cancellationToken))
            {
                count++;
            }

            RowsSeen += count;
            return count;
        }
    }
}
