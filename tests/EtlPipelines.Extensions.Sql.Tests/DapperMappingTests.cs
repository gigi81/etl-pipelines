namespace EtlPipelines.Sql.Tests;

/// <summary>
/// Reading rows without writing a mapping delegate.
/// </summary>
/// <remarks>
/// The mapping is Dapper's compiled row parser, built once against the columns the query returned.
/// What is worth pinning down is not that Dapper works but that this library hands it the right
/// thing: the parser is built after the reader is open, matches by name rather than by position, and
/// still produces one object per row rather than N views of a moving cursor.
/// </remarks>
public sealed class DapperMappingTests : IAsyncDisposable
{
    private const string Extract = "extract";

    private readonly SqliteHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>
    /// A record with settable properties rather than a positional one — see
    /// <see cref="Says_what_is_wrong_when_the_row_type_has_no_usable_constructor"/> for why that
    /// distinction matters to the automatic mapping.
    /// </summary>
    public sealed record Order
    {
        public long Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    /// <summary>A positional record, whose only constructor takes all three columns.</summary>
    public sealed record Positional(long Id, string Customer, decimal Amount);

    public sealed class Mutable
    {
        public long Id { get; set; }
        public string? Customer { get; set; }
    }

    private static Order Row(long id, string customer, decimal amount) =>
        new() { Id = id, Customer = customer, Amount = amount };

    private async Task SeedAsync(int count)
    {
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");

        for (var i = 1; i <= count; i++)
        {
            await _host.ExecuteAsync(
                $"INSERT INTO orders (Id, Customer, Amount) VALUES ({i}, 'customer-{i}', {i * 1.5m})");
        }
    }

    [Test]
    public async Task Maps_columns_to_properties_without_a_delegate()
    {
        //arrange
        await SeedAsync(3);
        var rows = new CollectingSink<Order>();

        _host.AddEtlPipeline(Extract, b => b
            .FromSql<Order>(_host.Open, "SELECT Id, Customer, Amount FROM orders ORDER BY Id")
            .To(_ => rows));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        rows.Rows.Should().Equal(
        [
            Row(1, "customer-1", 1.5m),
            Row(2, "customer-2", 3.0m),
            Row(3, "customer-3", 4.5m),
        ]);
    }

    [Test]
    public async Task Matches_by_name_rather_than_by_position()
    {
        //arrange
        await SeedAsync(1);
        var rows = new CollectingSink<Order>();

        // Deliberately the reverse of the property order. A positional mapper would put the amount
        // into Id and fail, or worse, not fail.
        _host.AddEtlPipeline(Extract, b => b
            .FromSql<Order>(_host.Open, "SELECT Amount, Customer, Id FROM orders")
            .To(_ => rows));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        rows.Rows.Should().ContainSingle().Which.Should().Be(Row(1, "customer-1", 1.5m));
    }

    [Test]
    public async Task Gives_every_row_its_own_object()
    {
        //arrange
        // The failure this guards against is a mapper that hands the reader itself downstream: the
        // batch then holds N references to one cursor, and every row reads as the last one.
        await SeedAsync(50);
        var rows = new CollectingSink<Mutable>();

        _host.AddEtlPipeline(Extract, b => b
            .WithOptions(options => options.BatchSize = 8)
            .FromSql<Mutable>(_host.Open, "SELECT Id, Customer FROM orders ORDER BY Id")
            .To(_ => rows));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        rows.Rows.Should().HaveCount(50);
        rows.Rows.Select(r => r.Id).Should().Equal(Enumerable.Range(1, 50).Select(i => (long)i));
        rows.Rows.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task Reads_a_null_column_as_null()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");
        await _host.ExecuteAsync("INSERT INTO orders (Id, Customer, Amount) VALUES (1, NULL, 0)");
        var rows = new CollectingSink<Mutable>();

        _host.AddEtlPipeline(Extract, b => b
            .FromSql<Mutable>(_host.Open, "SELECT Id, Customer FROM orders")
            .To(_ => rows));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        rows.Rows.Should().ContainSingle().Which.Customer.Should().BeNull();
    }

    [Test]
    public async Task Still_takes_a_hand_written_mapper_when_one_is_given()
    {
        //arrange
        await SeedAsync(2);
        var rows = new CollectingSink<Order>();

        _host.AddEtlPipeline(Extract, b => b
            .FromSql(
                _host.Open,
                "SELECT Id, Customer, Amount FROM orders ORDER BY Id",
                r => Row(r.GetInt64(0), r.GetString(1).ToUpperInvariant(), r.GetDecimal(2)))
            .To(_ => rows));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        rows.Rows.Select(r => r.Customer).Should().Equal(["CUSTOMER-1", "CUSTOMER-2"]);
    }

    [Test]
    public async Task Says_what_is_wrong_when_the_row_type_has_no_usable_constructor()
    {
        //arrange
        // A real constraint, worth pinning down rather than leaving to be discovered. The automatic
        // mapping matches a constructor against the types the *provider* reports, not the ones the
        // row type declares - and SQLite reports both NUMERIC and DECIMAL columns as Double. So a
        // positional record taking a decimal has no constructor Dapper will use, while the same
        // record with settable properties converts per column and is fine. The way out is either a
        // settable row type or a hand-written mapper.
        await SeedAsync(1);
        var rows = new CollectingSink<Positional>();

        _host.AddEtlPipeline(Extract, b => b
            .FromSql<Positional>(_host.Open, "SELECT Id, Customer, Amount FROM orders")
            .To(_ => rows));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should()
            .Contain("constructor").And
            .Contain(nameof(Positional));
    }

    [Test]
    public async Task Reads_a_column_whose_rows_are_not_all_stored_the_same_way()
    {
        //arrange
        // SQLite types a value, not a column. Under NUMERIC affinity 3.0 is stored as an integer
        // while 1.5 and 4.5 are stored as floats, so the column reports Int64 on one row and Double
        // on the next — and a parser compiled against either one alone cannot read the other.
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");
        await _host.ExecuteAsync(
            "INSERT INTO orders (Id, Customer, Amount) VALUES " +
            "(1, 'a', 1.5), (2, 'b', 3.0), (3, 'c', 4.5), (4, 'd', 6.0)");

        var rows = new CollectingSink<Order>();

        _host.AddEtlPipeline(Extract, b => b
            .FromSql<Order>(_host.Open, "SELECT Id, Customer, Amount FROM orders ORDER BY Id")
            .To(_ => rows));

        //act
        var result = await _host.RunAsync(Extract);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        rows.Rows.Select(r => r.Amount).Should().Equal([1.5m, 3m, 4.5m, 6m]);
    }
}
