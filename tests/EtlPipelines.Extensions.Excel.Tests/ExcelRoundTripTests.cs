using System.Globalization;

namespace EtlPipelines.Excel.Tests;

/// <summary>Writing a worksheet and reading it back.</summary>
public class ExcelRoundTripTests
{
    private const string PipelineName = "orders";
    private const string Write = "write";
    private const string Read = "read";

    private readonly ExcelTestHost _host = new();

    /// <summary>
    /// A row type shaped the way the reader needs: a parameterless constructor and settable
    /// properties, which is how worksheet columns are matched to it by name.
    /// </summary>
    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime Placed { get; set; }

        public override bool Equals(object? obj) =>
            obj is Order o && o.Id == Id && o.Customer == Customer && o.Amount == Amount && o.Placed == Placed;

        public override int GetHashCode() => HashCode.Combine(Id, Customer, Amount, Placed);

        public override string ToString() => $"{Id}/{Customer}/{Amount}/{Placed:d}";
    }

    private static Order[] Sample() =>
    [
        new() { Id = 1, Customer = "acme", Amount = 10.50m, Placed = new DateTime(2026, 1, 2) },
        new() { Id = 2, Customer = "globex, inc \"the best\"", Amount = -3.25m, Placed = new DateTime(2026, 3, 4) },
        new() { Id = 3, Customer = "wörks ünicode", Amount = 0m, Placed = new DateTime(2026, 5, 6) },
    ];

    [Test]
    public async Task Writes_rows_and_reads_them_back_unchanged()
    {
        //arrange
        var target = _host.File("orders.xlsx");
        var readBack = new CollectingSink<Order>();
        var original = Sample();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(original)).ToExcel(target))
             .AddEtlPipeline(Read, b => b.FromExcel<Order>(target).To(_ => readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);

        // Quoting, unicode, a negative and a zero all survive the trip through the workbook.
        readBack.Rows.Should().Equal(original);
    }

    [Test]
    // Not in parallel: assigns CultureInfo.CurrentCulture, which is not this test’s to share.
    [NotInParallel]
    public async Task Round_trips_a_decimal_under_a_comma_separator_culture()
    {
        //arrange
        // A worksheet stores a number as a number, not as text, so this should be immune to the host's
        // culture in a way a CSV is not. Asserting it keeps that property from regressing the moment
        // any value starts going through a string on the way in or out.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            var target = _host.File("culture.xlsx");
            var readBack = new CollectingSink<Order>();

            _host.AddEtlPipeline(Write, b => b
                     .From(new ArraySource<Order>([new() { Id = 1, Customer = "acme", Amount = 1234.56m }]))
                     .ToExcel(target))
                 .AddEtlPipeline(Read, b => b.FromExcel<Order>(target).To(_ => readBack));

            //act
            await _host.RunAsync(Write);
            var read = await _host.RunAsync(Read);

            //assert
            read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
            readBack.Rows.Should().ContainSingle();
            readBack.Rows[0].Amount.Should().Be(1234.56m, "the value must not pick up the host's separator");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Test]
    public async Task Streams_correctly_across_many_batch_boundaries()
    {
        //arrange
        // 5,000 rows against a 32-row batch is ~156 boundaries. A source that mishandles a partially
        // filled buffer, or a sink that loses whatever sits in its channel at a batch edge, shows up
        // here and nowhere in a three-row test.
        const int rows = 5_000;
        var target = _host.File("many.xlsx");
        var readBack = new CollectingSink<Order>();

        var original = Enumerable.Range(0, rows)
            .Select(i => new Order { Id = i, Customer = $"c{i}", Amount = i * 0.25m })
            .ToArray();

        _host.AddEtlPipeline(Write, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .From(new ArraySource<Order>(original))
                 .ToExcel(target))
             .AddEtlPipeline(Read, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .FromExcel<Order>(target)
                 .To(_ => readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);

        write.Value.RowsWritten.Should().Be(rows);
        read.Value.RowsRead.Should().Be(rows);
        readBack.Rows.Should().HaveCount(rows);
        readBack.Rows.Select(r => r.Id).Should().Equal(Enumerable.Range(0, rows), "no row may be dropped or reordered");
    }

    [Test]
    public async Task Reads_the_sheet_from_the_start_on_every_run()
    {
        //arrange
        var target = _host.File("repeat.xlsx");
        var sink = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Sample())).ToExcel(target))
             .AddEtlPipeline(Read, b => b.FromExcel<Order>(target).To(_ => sink));

        await _host.RunAsync(Write);

        //act
        var first = await _host.RunAsync(Read);
        var second = await _host.RunAsync(Read);

        //assert
        first.Value.RowsRead.Should().Be(3);
        second.Value.RowsRead.Should().Be(
            3,
            "the run scope builds its own source and reopens the workbook, rather than resuming at the end");
        sink.Rows.Should().HaveCount(6);
    }

    [Test]
    public async Task Writes_and_reads_a_named_worksheet()
    {
        //arrange
        var target = _host.File("named.xlsx");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b
                 .From(new ArraySource<Order>(Sample()))
                 .ToExcel(target, new ExcelSinkOptions { SheetName = "Orders" }))
             .AddEtlPipeline(Read, b => b
                 .FromExcel<Order>(target, new ExcelSourceOptions { SheetName = "Orders" })
                 .To(_ => readBack));

        //act
        await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().HaveCount(3);
    }

    [Test]
    public async Task Transforms_between_reading_and_writing()
    {
        //arrange
        var source = _host.File("in.xlsx");
        var converted = _host.File("out.xlsx");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline("seed", b => b.From(new ArraySource<Order>(Sample())).ToExcel(source))
             .AddEtlPipeline(PipelineName, b => b
                 .FromExcel<Order>(source)
                 .Where(o => o.Amount > 0)
                 .Select(o => new Order { Id = o.Id, Customer = o.Customer.ToUpperInvariant(), Amount = o.Amount * 100 })
                 .ToExcel(converted))
             .AddEtlPipeline(Read, b => b.FromExcel<Order>(converted).To(_ => readBack));

        await _host.RunAsync("seed");

        //act
        var run = await _host.RunAsync(PipelineName);
        await _host.RunAsync(Read);

        //assert
        run.IsError.Should().BeFalse(run.IsError ? run.FirstError.Description : null);

        // One of the three sample rows is negative and one is zero, so only one survives the filter.
        readBack.Rows.Should().ContainSingle();
        readBack.Rows[0].Customer.Should().Be("ACME");
        readBack.Rows[0].Amount.Should().Be(1050m);
    }

    [Test]
    public async Task Fans_the_same_rows_out_to_two_workbooks()
    {
        //arrange
        // A file sink per branch is the motivating case for branching, and two workbook writers running
        // at once is where a shared temp name or a shared channel would show up.
        var archive = _host.File("archive.xlsx");
        var converted = _host.File("converted.xlsx");
        var fromArchive = new CollectingSink<Order>();
        var fromConverted = new CollectingSink<Order>();

        _host.AddEtlPipeline(PipelineName, b => b
                 .From(new ArraySource<Order>(Sample()))
                 .Branch(
                     b1 => b1.ToExcel(archive),
                     b2 => b2.Select(o => new Order { Id = o.Id, Customer = o.Customer, Amount = o.Amount * 2 })
                             .ToExcel(converted)))
             .AddEtlPipeline("read-archive", b => b.FromExcel<Order>(archive).To(_ => fromArchive))
             .AddEtlPipeline("read-converted", b => b.FromExcel<Order>(converted).To(_ => fromConverted));

        //act
        var result = await _host.RunAsync(PipelineName);
        await _host.RunAsync("read-archive");
        await _host.RunAsync("read-converted");

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsWritten.Should().Be(6, "three rows reached each of the two workbooks");

        fromArchive.Rows.Should().HaveCount(3);
        fromConverted.Rows.Should().HaveCount(3);
        fromConverted.Rows.Select(r => r.Amount).Should().Equal(21.00m, -6.50m, 0m);
    }

    [Test]
    public async Task Writes_a_readable_workbook_when_there_are_no_rows_at_all()
    {
        //arrange
        // An empty result set is ordinary — a filter that matched nothing — and must still produce a
        // workbook that opens, not a zero-byte file or a failed run.
        var target = _host.File("empty.xlsx");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>([])).ToExcel(target))
             .AddEtlPipeline(Read, b => b.FromExcel<Order>(target).To(_ => readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);

        target.Refresh();
        target.Exists.Should().BeTrue();
        readBack.Rows.Should().BeEmpty();
    }
}
