using System.IO.Abstractions;
using EtlPipelines.Csv;
using FluentAssertions;

namespace EtlPipelines.Csv.Tests;

/// <summary>
/// The same ports against the real disk.
/// </summary>
/// <remarks>
/// Everything else in this suite runs on <c>MockFileSystem</c>, which is faster and needs no cleanup —
/// but it is a reimplementation, and it can differ from the real thing at the edges that matter here:
/// overwriting renames, and creating nested directories. These few tests exist so a divergence shows
/// up as a failure rather than as a production surprise.
/// </remarks>
public sealed class CsvRealFileSystemTests : IDisposable
{
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly IDirectoryInfo _root;

    public CsvRealFileSystemTests() =>
        _root = _fileSystem.Directory.CreateTempSubdirectory("etl-csv-real-");

    public void Dispose() => _root.Delete(recursive: true);

    private IFileInfo File(string name) => _root.File(name);

    private sealed record Order(int Id, string Customer, decimal Amount);

    [Fact]
    public async Task Round_trips_through_the_real_file_system()
    {
        var target = File("orders.csv");
        Order[] orders = [new(1, "acme", 10.50m), new(2, "Globex, Inc. \"HQ\"", 25.75m)];

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
    public async Task Renames_over_an_existing_file_on_the_real_disk()
    {
        // The atomic promotion depends on a move actually replacing a file that is already there.
        // Worth proving against a real filesystem, not only a simulated one.
        var target = File("twice.csv");

        for (var run = 1; run <= 2; run++)
        {
            var rows = Enumerable.Range(0, run * 5).Select(i => new Order(i, $"c{i}", i)).ToArray();

            var result = await EtlPipeline.CreateBuilder($"run{run}")
                .From(new ArraySource<Order>(rows))
                .ToCsv(target)
                .Build()
                .RunAsync(CancellationToken.None);

            result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        }

        var lines = await target.ReadAllLinesAsync(CancellationToken.None);
        lines.Should().HaveCount(11, "the second run replaced the first file wholesale");
        _root.EnumerateFiles("*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Creates_nested_directories_on_the_real_disk()
    {
        var target = _root.SubDirectory("nested", "deeper").File("out.csv");

        var result = await EtlPipeline.CreateBuilder("nested")
            .From(new ArraySource<Order>([new(1, "acme", 1m)]))
            .ToCsv(target)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        target.Refresh();
        target.Exists.Should().BeTrue();
    }
}
