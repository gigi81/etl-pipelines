using System.Globalization;
using System.IO.Abstractions;
using EtlPipelines.Abstractions.Ports;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Csv.Tests;

/// <summary>Reading and writing CSV through pipelines resolved from a container.</summary>
public sealed class CsvRoundTripTests
{
    private const string PipelineName = "orders";
    private const string Write = "write";
    private const string Read = "read";

    private readonly CsvTestHost _host = new();

    public sealed record Order(int Id, string Customer, decimal Amount);

    public sealed record OrderDto(int Id, string Customer, decimal AmountInCents);

    private static readonly Order[] Orders =
    [
        new(1, "acme", 10.50m),
        // A comma and a quote in the same value: if quoting is wrong this row splits into extra
        // fields and the read back fails or silently shifts every column.
        new(2, "Globex, Inc. \"HQ\"", 25.75m),
        new(3, "initech", 3.99m),
    ];

    [Fact]
    public async Task Round_trips_rows_through_a_file()
    {
        //arrange
        var target = _host.File("orders.csv");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Orders)).ToCsv(target))
             .AddEtlPipeline(Read, b => b.FromCsv<Order>(target).To(readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().Equal(Orders);
    }

    [Fact]
    public async Task Resolves_both_ports_from_the_container()
    {
        //arrange
        // The sink is named only by type; the container supplies it. This is the shape an application
        // actually writes, and it only works because composing the pipeline registers the CSV source
        // alongside whatever else is in the container.
        var target = _host.File("resolved.csv");
        var readBack = new CollectingSink<Order>();

        _host.Configure(s => s.AddSingleton<IDataSink<Order>>(readBack))
             .AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Orders)).ToCsv(target))
             .AddEtlPipeline(Read, b => b.FromCsv<Order>(target).To());

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().Equal(Orders);
    }

    [Fact]
    public void Registers_csv_ports_as_scoped_and_keyed()
    {
        //arrange
        _host.AddEtlPipeline(PipelineName, b => b
            .FromCsv<Order>(_host.File("in.csv"))
            .ToCsv(_host.File("out.csv")));

        //act
        var ports = _host.Registrations
            .Where(d => d.ServiceType == typeof(IDataSource<Order>) || d.ServiceType == typeof(IDataSink<Order>))
            .ToArray();

        //assert
        // The CSV ports are ordinary components: composing the pipeline registers them, scoped so each
        // run gets its own file handles, keyed so a second pipeline reading CSV does not collide.
        ports.Should().HaveCount(2);
        ports.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
        ports.Should().OnlyContain(d => d.IsKeyedService);
        ports.Should().OnlyContain(d => d.ServiceKey!.ToString()!.StartsWith($"{PipelineName}["));
    }

    [Fact]
    public async Task Formats_numbers_independently_of_the_machine_culture()
    {
        //arrange
        // Under a comma-decimal culture, 10.50 would be written as "10,50" and then split into two
        // fields by the comma delimiter — corrupting every subsequent column. Invariant culture is
        // the default precisely so a data file means the same thing wherever it is processed.
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");

        try
        {
            var target = _host.File("culture.csv");
            var readBack = new CollectingSink<Order>();

            _host.AddEtlPipeline(Write, b => b
                     .From(new ArraySource<Order>([new(1, "acme", 1234.56m)]))
                     .ToCsv(target))
                 .AddEtlPipeline(Read, b => b.FromCsv<Order>(target).To(readBack));

            //act
            await _host.RunAsync(Write);
            var text = await target.ReadAllTextAsync(CancellationToken.None);
            await _host.RunAsync(Read);

            //assert
            text.Should().Contain("1234.56", "the file must use the invariant separator, not the host's");
            readBack.Rows.Should().Equal([new Order(1, "acme", 1234.56m)]);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Streams_correctly_across_many_batch_boundaries()
    {
        //arrange
        var target = _host.File("many.csv");
        var orders = Enumerable.Range(0, 2_500)
            .Select(i => new Order(i, $"customer-{i}", i * 1.5m))
            .ToArray();
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .From(new ArraySource<Order>(orders))
                 .ToCsv(target))
             .AddEtlPipeline(Read, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .FromCsv<Order>(target)
                 .To(readBack));

        //act
        await _host.RunAsync(Write);
        var result = await _host.RunAsync(Read);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        readBack.Rows.Should().Equal(orders, "a partially filled buffer must not lose or repeat rows");
        result.Value.RowsRead.Should().Be(2_500);
    }

    [Fact]
    public async Task Transforms_between_two_files()
    {
        //arrange
        var source = _host.File("in.csv");
        var target = _host.File("out.csv");

        _host.AddEtlPipeline(Write, b => b
                 .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
                 .ToCsv(source))
             .AddEtlPipeline(Read, b => b
                 .FromCsv<Order>(source)
                 .Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
                 .ToCsv(target));

        //act
        await _host.RunAsync(Write);
        var result = await _host.RunAsync(Read);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        var lines = await target.ReadAllLinesAsync(CancellationToken.None);
        lines[0].Should().Be("Id,Customer,AmountInCents");

        // 1050.00, not 1050: decimal multiplication preserves scale, and CsvHelper writes the value
        // as it is rather than trimming it. Worth pinning, since a downstream strict parser cares.
        lines[1].Should().Be("1,acme,1050.00");
    }

    [Fact]
    public async Task Fans_one_source_out_to_two_files()
    {
        //arrange
        // A file sink per branch is the motivating case for branching: archive the raw rows while the
        // same rows carry on into a reshaped load.
        var archive = _host.File("archive.csv");
        var converted = _host.File("converted.csv");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
            .Branch(
                b1 => b1.ToCsv(archive),
                b2 => b2.Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100)).ToCsv(converted)));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await archive.ReadAllLinesAsync(CancellationToken.None)).Should().HaveCount(3);
        (await converted.ReadAllLinesAsync(CancellationToken.None)).Should().HaveCount(3);
        result.Value.RowsWritten.Should().Be(4, "two rows reached each of the two files");
    }

    [Fact]
    public async Task Reads_the_file_from_the_start_on_every_run()
    {
        //arrange
        var target = _host.File("repeat.csv");
        await target.WriteAllTextAsync(
            "Id,Customer,Amount\n1,acme,1.00\n2,globex,2.00\n",
            CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(PipelineName, b => b.FromCsv<Order>(target).To(_ => sink));

        //act
        var first = await _host.RunAsync(PipelineName);
        var second = await _host.RunAsync(PipelineName);

        //assert
        first.Value.RowsRead.Should().Be(2);
        second.Value.RowsRead.Should().Be(
            2,
            "the run scope builds its own source and reopens the file, rather than resuming at EOF");
        sink.Rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task Reads_an_empty_file_as_no_rows()
    {
        //arrange
        var target = _host.File("empty.csv");
        await target.WriteAllTextAsync(string.Empty, CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(PipelineName, b => b.FromCsv<Order>(target).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse("an empty file is not a failure");
        sink.Rows.Should().BeEmpty();
    }
}
