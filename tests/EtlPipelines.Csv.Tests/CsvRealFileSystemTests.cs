using System.IO.Abstractions;

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
public sealed class CsvRealFileSystemTests
{
    private const string PipelineName = "orders";
    private const string Write = "write";
    private const string Read = "read";
    private const string Rewrite = "rewrite";

    private readonly CsvTestHost _host = new(new FileSystem());

    /// <summary>Removes the scratch directory this test wrote to on the real disk.</summary>
    [After(Test)]
    public void DeleteScratchDirectory() => _host.Root.Delete(recursive: true);

    private sealed record Order(int Id, string Customer, decimal Amount);

    private static Order[] Orders(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new Order(i, $"c{i}", i))
            .ToArray();

    [Test]
    public async Task Round_trips_through_the_real_file_system()
    {
        //arrange
        var target = _host.File("orders.csv");
        Order[] orders = [new(1, "acme", 10.50m), new(2, "Globex, Inc. \"HQ\"", 25.75m)];
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(orders)).ToCsv(target))
             .AddEtlPipeline(Read, b => b.FromCsv<Order>(target).To(readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);
        readBack.Rows.Should().Equal(orders);
    }

    [Test]
    public async Task Renames_over_an_existing_file_on_the_real_disk()
    {
        //arrange
        // The atomic promotion depends on a move actually replacing a file that is already there.
        // Worth proving against a real filesystem, not only a simulated one.
        var target = _host.File("twice.csv");

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Orders(5))).ToCsv(target))
             .AddEtlPipeline(Rewrite, b => b.From(new ArraySource<Order>(Orders(10))).ToCsv(target));

        //act
        var first = await _host.RunAsync(Write);
        var second = await _host.RunAsync(Rewrite);

        //assert
        first.IsError.Should().BeFalse(first.IsError ? first.FirstError.Description : null);
        second.IsError.Should().BeFalse(second.IsError ? second.FirstError.Description : null);

        var lines = await target.ReadAllLinesAsync(CancellationToken.None);
        lines.Should().HaveCount(11, "the second run replaced the first file wholesale");
        _host.TempFiles().Should().BeEmpty();
    }

    [Test]
    public async Task Creates_nested_directories_on_the_real_disk()
    {
        //arrange
        var target = _host.Root.SubDirectory("nested", "deeper").File("out.csv");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>([new(1, "acme", 1m)]))
            .ToCsv(target));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        target.Refresh();
        target.Exists.Should().BeTrue();
    }
}
