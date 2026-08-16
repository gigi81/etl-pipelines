using System.Globalization;
using System.IO.Abstractions;
using EtlPipelines.Csv;
using FluentAssertions;

namespace EtlPipelines.Csv.Tests;

/// <summary>Reading and writing CSV through a real pipeline, against an in-memory filesystem.</summary>
public sealed class CsvRoundTripTests
{
    private readonly TestFileSystem _fs = new();

    public sealed record Order(int Id, string Customer, decimal Amount);

    public sealed record OrderDto(int Id, string Customer, decimal AmountInCents);

    [Fact]
    public async Task Round_trips_rows_through_a_file()
    {
        var target = _fs.Path("orders.csv");

        Order[] orders =
        [
            new(1, "acme", 10.50m),
            // A comma and a quote in the same value: if quoting is wrong this row splits into extra
            // fields and the read back fails or silently shifts every column.
            new(2, "Globex, Inc. \"HQ\"", 25.75m),
            new(3, "initech", 3.99m),
        ];

        var write = await EtlPipeline.CreateBuilder("write")
            .From(new ArraySource<Order>(orders))
            .ToCsv(target, fileSystem: _fs.FileSystem)
            .Build()
            .RunAsync(CancellationToken.None);

        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);

        var readBack = new CollectingSink<Order>();
        var read = await EtlPipeline.CreateBuilder("read")
            .FromCsv<Order>(target, fileSystem: _fs.FileSystem)
            .To(readBack)
            .Build()
            .RunAsync(CancellationToken.None);

        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().Equal(orders);
    }

    [Fact]
    public async Task Formats_numbers_independently_of_the_machine_culture()
    {
        // Under a comma-decimal culture, 10.50 would be written as "10,50" and then split into two
        // fields by the comma delimiter — corrupting every subsequent column. Invariant culture is
        // the default precisely so a data file means the same thing wherever it is processed.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            var target = _fs.Path("culture.csv");
            Order[] orders = [new(1, "acme", 1234.56m)];

            await EtlPipeline.CreateBuilder("write")
                .From(new ArraySource<Order>(orders))
                .ToCsv(target, fileSystem: _fs.FileSystem)
                .Build()
                .RunAsync(CancellationToken.None);

            var text = await _fs.File("culture.csv").ReadAllTextAsync(CancellationToken.None);
            text.Should().Contain("1234.56", "the file must use the invariant separator, not the host's");

            var readBack = new CollectingSink<Order>();
            await EtlPipeline.CreateBuilder("read")
                .FromCsv<Order>(target, fileSystem: _fs.FileSystem)
                .To(readBack)
                .Build()
                .RunAsync(CancellationToken.None);

            readBack.Rows.Should().Equal(orders);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Streams_correctly_across_many_batch_boundaries()
    {
        var target = _fs.Path("many.csv");
        var orders = Enumerable.Range(0, 2_500)
            .Select(i => new Order(i, $"customer-{i}", i * 1.5m))
            .ToArray();

        await EtlPipeline.CreateBuilder("write")
            .WithOptions(o => o.BatchSize = 32)
            .From(new ArraySource<Order>(orders))
            .ToCsv(target, fileSystem: _fs.FileSystem)
            .Build()
            .RunAsync(CancellationToken.None);

        var readBack = new CollectingSink<Order>();
        var result = await EtlPipeline.CreateBuilder("read")
            .WithOptions(o => o.BatchSize = 32)
            .FromCsv<Order>(target, fileSystem: _fs.FileSystem)
            .To(readBack)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        readBack.Rows.Should().Equal(orders, "a partially filled buffer must not lose or repeat rows");
        result.Value.RowsRead.Should().Be(2_500);
    }

    [Fact]
    public async Task Transforms_between_two_files()
    {
        var source = _fs.Path("in.csv");
        var target = _fs.Path("out.csv");

        await EtlPipeline.CreateBuilder("seed")
            .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
            .ToCsv(source, fileSystem: _fs.FileSystem)
            .Build()
            .RunAsync(CancellationToken.None);

        var result = await EtlPipeline.CreateBuilder("convert")
            .FromCsv<Order>(source, fileSystem: _fs.FileSystem)
            .Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
            .ToCsv(target, fileSystem: _fs.FileSystem)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        var lines = await _fs.File("out.csv").ReadAllLinesAsync(CancellationToken.None);
        lines[0].Should().Be("Id,Customer,AmountInCents");

        // 1050.00, not 1050: decimal multiplication preserves scale, and CsvHelper writes the value
        // as it is rather than trimming it. Worth pinning, since a downstream strict parser cares.
        lines[1].Should().Be("1,acme,1050.00");
    }

    [Fact]
    public async Task Fans_one_source_out_to_two_files()
    {
        // A file sink per branch is the motivating case for branching: archive the raw rows while the
        // same rows carry on into a reshaped load.
        var result = await EtlPipeline.CreateBuilder("fanout")
            .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
            .Branch(
                b1 => b1.ToCsv(_fs.Path("archive.csv"), fileSystem: _fs.FileSystem),
                b2 => b2.Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
                        .ToCsv(_fs.Path("converted.csv"), fileSystem: _fs.FileSystem))
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        _fs.File("archive.csv").Exists.Should().BeTrue();
        _fs.File("converted.csv").Exists.Should().BeTrue();
        (await _fs.File("archive.csv").ReadAllLinesAsync(CancellationToken.None)).Should().HaveCount(3);
        (await _fs.File("converted.csv").ReadAllLinesAsync(CancellationToken.None)).Should().HaveCount(3);
        result.Value.RowsWritten.Should().Be(4, "two rows reached each of the two files");
    }

    [Fact]
    public async Task Reads_the_file_from_the_start_on_every_run()
    {
        var target = _fs.Path("repeat.csv");
        await _fs.File("repeat.csv")
            .WriteAllTextAsync("Id,Customer,Amount\n1,acme,1.00\n2,globex,2.00\n", CancellationToken.None);

        var sink = new CollectingSink<Order>();
        var pipeline = EtlPipeline.CreateBuilder("repeat")
            .FromCsv<Order>(target, fileSystem: _fs.FileSystem)
            .To(_ => sink)
            .Build();

        var first = await pipeline.RunAsync(CancellationToken.None);
        var countAfterFirst = sink.Rows.Count;
        var second = await pipeline.RunAsync(CancellationToken.None);

        first.Value.RowsRead.Should().Be(2);
        second.Value.RowsRead.Should().Be(
            2,
            "each run builds its own source and reopens the file, rather than resuming at EOF");
        sink.Rows.Should().HaveCount(countAfterFirst * 2);
    }

    [Fact]
    public async Task Reads_an_empty_file_as_no_rows()
    {
        await _fs.File("empty.csv").WriteAllTextAsync(string.Empty, CancellationToken.None);

        var sink = new CollectingSink<Order>();
        var result = await EtlPipeline.CreateBuilder("empty")
            .FromCsv<Order>(_fs.Path("empty.csv"), fileSystem: _fs.FileSystem)
            .To(sink)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse("an empty file is not a failure");
        sink.Rows.Should().BeEmpty();
    }
}
