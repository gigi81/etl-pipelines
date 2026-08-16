using System.IO.Abstractions;
using FluentAssertions;

namespace EtlPipelines.Csv.Tests;

/// <summary>
/// The same pipelines against the real disk.
/// </summary>
/// <remarks>
/// Everything else in this suite runs on <c>MockFileSystem</c>, which is faster and needs no cleanup —
/// but it is a reimplementation, and it can differ from the real thing at the edges that matter here:
/// overwriting renames, and creating nested directories. These few tests exist so a divergence shows
/// up as a failure rather than as a production surprise. The only thing that changes is which
/// <see cref="IFileSystem"/> the container is given.
/// </remarks>
public sealed class CsvRealFileSystemTests : IDisposable
{
    private readonly CsvTestHost _host = new(new FileSystem());

    public void Dispose() => _host.Root.Delete(recursive: true);

    private sealed record Order(int Id, string Customer, decimal Amount);

    [Fact]
    public async Task Round_trips_through_the_real_file_system()
    {
        var target = _host.File("orders.csv");
        Order[] orders = [new(1, "acme", 10.50m), new(2, "Globex, Inc. \"HQ\"", 25.75m)];
        var readBack = new CollectingSink<Order>();

        _host.AddPipeline("write", b => b.From(new ArraySource<Order>(orders)).ToCsv(target))
             .AddPipeline("read", b => b.FromCsv<Order>(target).To(readBack));

        var write = await _host.RunAsync("write");
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);

        var read = await _host.RunAsync("read");
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);

        readBack.Rows.Should().Equal(orders);
    }

    [Fact]
    public async Task Renames_over_an_existing_file_on_the_real_disk()
    {
        // The atomic promotion depends on a move actually replacing a file that is already there.
        // Worth proving against a real filesystem, not only a simulated one.
        var target = _host.File("twice.csv");

        _host.AddPipeline("first", b => b
                 .From(new ArraySource<Order>(Orders(5)))
                 .ToCsv(target))
             .AddPipeline("second", b => b
                 .From(new ArraySource<Order>(Orders(10)))
                 .ToCsv(target));

        (await _host.RunAsync("first")).IsError.Should().BeFalse();
        (await _host.RunAsync("second")).IsError.Should().BeFalse();

        var lines = await target.ReadAllLinesAsync(CancellationToken.None);
        lines.Should().HaveCount(11, "the second run replaced the first file wholesale");
        _host.TempFiles().Should().BeEmpty();

        static Order[] Orders(int count) =>
            [.. Enumerable.Range(0, count).Select(i => new Order(i, $"c{i}", i))];
    }

    [Fact]
    public async Task Creates_nested_directories_on_the_real_disk()
    {
        var target = _host.Root.SubDirectory("nested", "deeper").File("out.csv");

        _host.AddPipeline("nested", b => b
            .From(new ArraySource<Order>([new(1, "acme", 1m)]))
            .ToCsv(target));

        var result = await _host.RunAsync("nested");

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        target.Refresh();
        target.Exists.Should().BeTrue();
    }
}
