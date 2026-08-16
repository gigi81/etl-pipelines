using System.Globalization;
using EtlPipelines.Csv;
using FluentAssertions;

namespace EtlPipelines.Csv.Tests;

/// <summary>Reading and writing CSV through a real pipeline.</summary>
public sealed class CsvRoundTripTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etl-csv-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    public sealed record Order(int Id, string Customer, decimal Amount);

    public sealed record OrderDto(int Id, string Customer, decimal AmountInCents);

    [Fact]
    public async Task Round_trips_rows_through_a_file()
    {
        var target = Path("orders.csv");

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
            .ToCsv(target)
            .Build()
            .RunAsync(CancellationToken.None);

        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);

        var readBack = new CollectingSink<Order>();
        var read = await EtlPipeline.CreateBuilder("read")
            .FromCsv<Order>(target)
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
            var target = Path("culture.csv");
            Order[] orders = [new(1, "acme", 1234.56m)];

            await EtlPipeline.CreateBuilder("write")
                .From(new ArraySource<Order>(orders))
                .ToCsv(target)
                .Build()
                .RunAsync(CancellationToken.None);

            var text = await File.ReadAllTextAsync(target, CancellationToken.None);
            text.Should().Contain("1234.56", "the file must use the invariant separator, not the host's");

            var readBack = new CollectingSink<Order>();
            await EtlPipeline.CreateBuilder("read")
                .FromCsv<Order>(target)
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
        var target = Path("many.csv");
        var orders = Enumerable.Range(0, 2_500)
            .Select(i => new Order(i, $"customer-{i}", i * 1.5m))
            .ToArray();

        await EtlPipeline.CreateBuilder("write")
            .WithOptions(o => o.BatchSize = 32)
            .From(new ArraySource<Order>(orders))
            .ToCsv(target)
            .Build()
            .RunAsync(CancellationToken.None);

        var readBack = new CollectingSink<Order>();
        var result = await EtlPipeline.CreateBuilder("read")
            .WithOptions(o => o.BatchSize = 32)
            .FromCsv<Order>(target)
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
        var source = Path("in.csv");
        var target = Path("out.csv");

        await EtlPipeline.CreateBuilder("seed")
            .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
            .ToCsv(source)
            .Build()
            .RunAsync(CancellationToken.None);

        var result = await EtlPipeline.CreateBuilder("convert")
            .FromCsv<Order>(source)
            .Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
            .ToCsv(target)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        var lines = await File.ReadAllLinesAsync(target, CancellationToken.None);
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
        var archive = Path("archive.csv");
        var converted = Path("converted.csv");

        var result = await EtlPipeline.CreateBuilder("fanout")
            .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
            .Branch(
                b1 => b1.ToCsv(archive),
                b2 => b2.Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100)).ToCsv(converted))
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        File.Exists(archive).Should().BeTrue();
        File.Exists(converted).Should().BeTrue();
        (await File.ReadAllLinesAsync(archive, CancellationToken.None)).Should().HaveCount(3);
        (await File.ReadAllLinesAsync(converted, CancellationToken.None)).Should().HaveCount(3);
        result.Value.RowsWritten.Should().Be(4, "two rows reached each of the two files");
    }

    [Fact]
    public async Task Reads_the_file_from_the_start_on_every_run()
    {
        var target = Path("repeat.csv");
        await File.WriteAllTextAsync(target, "Id,Customer,Amount\n1,acme,1.00\n2,globex,2.00\n", CancellationToken.None);

        var sink = new CollectingSink<Order>();
        var pipeline = EtlPipeline.CreateBuilder("repeat")
            .FromCsv<Order>(target)
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
        var target = Path("empty.csv");
        await File.WriteAllTextAsync(target, string.Empty, CancellationToken.None);

        var sink = new CollectingSink<Order>();
        var result = await EtlPipeline.CreateBuilder("empty")
            .FromCsv<Order>(target)
            .To(sink)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse("an empty file is not a failure");
        sink.Rows.Should().BeEmpty();
    }
}
