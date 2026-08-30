using System.IO.Abstractions;
using EtlPipelines.Abstractions.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Extensions.Json.Tests;

/// <summary>Reading and writing JSON through pipelines resolved from a container.</summary>
public sealed class JsonRoundTripTests
{
    private const string PipelineName = "orders";
    private const string Write = "write";
    private const string Read = "read";

    private readonly JsonTestHost _host = new();

    public sealed record Order(int Id, string Customer, decimal Amount);

    public sealed record OrderDto(int Id, string Customer, decimal AmountInCents);

    private static readonly Order[] Orders =
    [
        new(1, "acme", 10.50m),
        // A comma and a quote in the same value: unremarkable for a JSON string, but the CSV
        // extension's equivalent fixture exists specifically because it is not for CSV - kept here so
        // the two suites stay comparable.
        new(2, "Globex, Inc. \"HQ\"", 25.75m),
        new(3, "initech", 3.99m),
    ];

    [Test]
    public async Task Round_trips_rows_through_a_JSON_Lines_file()
    {
        //arrange
        var target = _host.File("orders.json");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Orders)).ToJsonLines(target))
             .AddEtlPipeline(Read, b => b.FromJsonLines<Order>(target).To(readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().Equal(Orders);
    }

    [Test]
    public async Task Round_trips_rows_through_a_JSON_array_file()
    {
        //arrange
        var target = _host.File("orders.json");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Orders)).ToJsonArray(target))
             .AddEtlPipeline(Read, b => b.FromJsonArray<Order>(target).To(readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().Equal(Orders);
    }

    [Test]
    public async Task Resolves_both_ports_from_the_container()
    {
        //arrange
        // The sink is named only by type; the container supplies it. This is the shape an application
        // actually writes, and it only works because composing the pipeline registers the JSON source
        // alongside whatever else is in the container.
        var target = _host.File("resolved.json");
        var readBack = new CollectingSink<Order>();

        _host.Configure(s => s.AddSingleton<IDataSink<Order>>(readBack))
             .AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Orders)).ToJsonLines(target))
             .AddEtlPipeline(Read, b => b.FromJsonLines<Order>(target).To());

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().Equal(Orders);
    }

    [Test]
    public void Registers_json_ports_as_scoped_and_keyed()
    {
        //arrange
        _host.AddEtlPipeline(PipelineName, b => b
            .FromJsonLines<Order>(_host.File("in.json"))
            .ToJsonLines(_host.File("out.json")));

        //act
        var ports = _host.Registrations
            .Where(d => d.ServiceType == typeof(IDataSource<Order>) || d.ServiceType == typeof(IDataSink<Order>))
            .ToArray();

        //assert
        // The JSON ports are ordinary components: composing the pipeline registers them, scoped so
        // each run gets its own file handles, keyed so a second pipeline reading JSON does not
        // collide.
        ports.Should().HaveCount(2);
        ports.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
        ports.Should().OnlyContain(d => d.IsKeyedService);
        ports.Should().OnlyContain(d => d.ServiceKey!.ToString()!.StartsWith($"{PipelineName}["));
    }

    [Test]
    public async Task Streams_correctly_across_many_batch_boundaries_with_Lines()
    {
        //arrange
        var target = _host.File("many.json");
        var orders = Enumerable.Range(0, 2_500)
            .Select(i => new Order(i, $"customer-{i}", i * 1.5m))
            .ToArray();
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .From(new ArraySource<Order>(orders))
                 .ToJsonLines(target))
             .AddEtlPipeline(Read, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .FromJsonLines<Order>(target)
                 .To(readBack));

        //act
        await _host.RunAsync(Write);
        var result = await _host.RunAsync(Read);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        readBack.Rows.Should().Equal(orders, "a partially filled buffer must not lose or repeat rows");
        result.Value.RowsRead.Should().Be(2_500);
    }

    [Test]
    public async Task Streams_correctly_across_many_batch_boundaries_with_Array()
    {
        //arrange
        var target = _host.File("many-array.json");
        var orders = Enumerable.Range(0, 2_500)
            .Select(i => new Order(i, $"customer-{i}", i * 1.5m))
            .ToArray();
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .From(new ArraySource<Order>(orders))
                 .ToJsonArray(target))
             .AddEtlPipeline(Read, b => b
                 .WithOptions(o => o.BatchSize = 32)
                 .FromJsonArray<Order>(target)
                 .To(readBack));

        //act
        await _host.RunAsync(Write);
        var result = await _host.RunAsync(Read);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        readBack.Rows.Should().Equal(orders, "a partially filled buffer must not lose or repeat rows");
        result.Value.RowsRead.Should().Be(2_500);
    }

    [Test]
    public async Task Transforms_between_two_files()
    {
        //arrange
        var source = _host.File("in.json");
        var target = _host.File("out.json");

        _host.AddEtlPipeline(Write, b => b
                 .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
                 .ToJsonLines(source))
             .AddEtlPipeline(Read, b => b
                 .FromJsonLines<Order>(source)
                 .Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100))
                 .ToJsonLines(target));

        //act
        await _host.RunAsync(Write);
        var result = await _host.RunAsync(Read);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        var lines = await target.ReadAllLinesAsync(CancellationToken.None);
        lines.Should().HaveCount(2, "JSON Lines writes one record per line");

        // camelCase and no padding: the default SerializerOptions come from
        // JsonSerializerDefaults.Web, and decimal multiplication preserves scale the same way the
        // CSV extension's equivalent test pins.
        lines[0].Should().Be("""{"id":1,"customer":"acme","amountInCents":1050.00}""");
    }

    [Test]
    public async Task Fans_one_source_out_to_two_files()
    {
        //arrange
        // A file sink per branch is the motivating case for branching: archive the raw rows while the
        // same rows carry on into a reshaped load.
        var archive = _host.File("archive.json");
        var converted = _host.File("converted.json");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
            .Branch(
                b1 => b1.ToJsonLines(archive),
                b2 => b2.Select(o => new OrderDto(o.Id, o.Customer, o.Amount * 100)).ToJsonLines(converted)));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await archive.ReadAllLinesAsync(CancellationToken.None)).Should().HaveCount(2);
        (await converted.ReadAllLinesAsync(CancellationToken.None)).Should().HaveCount(2);
        result.Value.RowsWritten.Should().Be(4, "two rows reached each of the two files");
    }

    [Test]
    public async Task Reads_the_file_from_the_start_on_every_run()
    {
        //arrange
        var target = _host.File("repeat.json");
        await target.WriteAllTextAsync(
            "{\"id\":1,\"customer\":\"acme\",\"amount\":1.00}\n{\"id\":2,\"customer\":\"globex\",\"amount\":2.00}\n",
            CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(PipelineName, b => b.FromJsonLines<Order>(target).To(_ => sink));

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

    [Test]
    public async Task Reads_an_empty_lines_file_as_no_rows()
    {
        //arrange
        // Unlike the array shape, an empty JSON Lines file is a legitimate, if unusual, zero-row
        // file: there is simply nothing to iterate.
        var target = _host.File("empty.json");
        await target.WriteAllTextAsync(string.Empty, CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(PipelineName, b => b.FromJsonLines<Order>(target).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse("an empty file is not a failure");
        sink.Rows.Should().BeEmpty();
    }

    [Test]
    public async Task Reads_an_empty_array_as_no_rows()
    {
        //arrange
        // "[]" is the array shape's empty file. A zero-byte file is not valid JSON here - there is no
        // array to find - so it is not the equivalent case.
        var target = _host.File("empty.json");
        await target.WriteAllTextAsync("[]", CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(PipelineName, b => b.FromJsonArray<Order>(target).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse("an empty array is not a failure");
        sink.Rows.Should().BeEmpty();
    }

    [Test]
    public async Task Writes_a_single_json_array()
    {
        //arrange
        var target = _host.File("array.json");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>([new(1, "acme", 10.50m), new(2, "globex", 3.25m)]))
            .ToJsonArray(target));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        var text = await target.ReadAllTextAsync(CancellationToken.None);
        text.Should().Be(
            """[{"id":1,"customer":"acme","amount":10.50},{"id":2,"customer":"globex","amount":3.25}]""");
    }

    [Test]
    public async Task Reads_hand_written_PascalCase_JSON_case_insensitively()
    {
        //arrange
        // The default SerializerOptions come from JsonSerializerDefaults.Web: case-insensitive
        // property matching on read, so a file that was not produced by this library - most JSON
        // files are not - still binds.
        var target = _host.File("pascal.json");
        await target.WriteAllTextAsync(
            """{"Id":1,"Customer":"acme","Amount":10.50}""",
            CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(PipelineName, b => b.FromJsonLines<Order>(target).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().Equal(new Order(1, "acme", 10.50m));
    }
}
