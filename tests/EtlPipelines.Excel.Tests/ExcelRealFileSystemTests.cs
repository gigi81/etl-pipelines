using System.IO.Abstractions;
using EtlPipelines.Core;
using FluentAssertions;

namespace EtlPipelines.Excel.Tests;

/// <summary>
/// The same pipelines against the real disk.
/// </summary>
/// <remarks>
/// Everything else in this suite runs on <c>MockFileSystem</c>, which is faster and needs no cleanup —
/// but it is a reimplementation, and it can differ from the real thing at the edges that matter here:
/// overwriting renames, and creating nested directories. A workbook adds one more reason to check:
/// it is a zip archive built by seeking around a stream, which is exactly the kind of thing an
/// in-memory stream can be more forgiving about than a file on disk. The only thing that changes is
/// which <see cref="IFileSystem"/> the container is given.
/// </remarks>
public sealed class ExcelRealFileSystemTests : IDisposable
{
    private const string PipelineName = "orders";
    private const string Write = "write";
    private const string Read = "read";
    private const string Rewrite = "rewrite";

    private readonly ExcelTestHost _host = new(new FileSystem());

    public void Dispose() => _host.Root.Delete(recursive: true);

    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    private static Order[] Orders(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Order { Id = i, Customer = $"c{i}", Amount = i })];

    [Fact]
    public async Task Round_trips_through_the_real_file_system()
    {
        //arrange
        var target = _host.File("orders.xlsx");
        var orders = new[]
        {
            new Order { Id = 1, Customer = "acme", Amount = 10.50m },
            new Order { Id = 2, Customer = "Globex, Inc. \"HQ\"", Amount = 25.75m },
        };
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(orders)).ToExcel(target))
             .AddEtlPipeline(Read, b => b.FromExcel<Order>(target).To(_ => readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);

        readBack.Rows.Should().HaveCount(2);
        readBack.Rows.Select(r => r.Customer).Should().Equal("acme", "Globex, Inc. \"HQ\"");
        readBack.Rows.Select(r => r.Amount).Should().Equal(10.50m, 25.75m);
    }

    [Fact]
    public async Task Renames_over_an_existing_file_on_the_real_disk()
    {
        //arrange
        // The atomic promotion depends on a move actually replacing a file that is already there.
        // Worth proving against a real filesystem, not only a simulated one.
        var target = _host.File("twice.xlsx");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b.From(new ArraySource<Order>(Orders(5))).ToExcel(target))
             .AddEtlPipeline(Rewrite, b => b.From(new ArraySource<Order>(Orders(10))).ToExcel(target))
             .AddEtlPipeline(Read, b => b.FromExcel<Order>(target).To(_ => readBack));

        //act
        var first = await _host.RunAsync(Write);
        var second = await _host.RunAsync(Rewrite);
        var read = await _host.RunAsync(Read);

        //assert
        first.IsError.Should().BeFalse(first.IsError ? first.FirstError.Description : null);
        second.IsError.Should().BeFalse(second.IsError ? second.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);

        readBack.Rows.Should().HaveCount(10, "the second run replaced the first workbook wholesale");
        _host.TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Creates_nested_directories_on_the_real_disk()
    {
        //arrange
        var target = _host.Root.SubDirectory("nested", "deeper").File("out.xlsx");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>(Orders(1)))
            .ToExcel(target));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        target.Refresh();
        target.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task Streams_a_large_sheet_through_the_pipeline()
    {
        //arrange
        // The reason this connector exists on MiniExcel rather than a DOM-based library: the workbook
        // is written and read as a stream, so a sheet far larger than a comfortable in-memory
        // representation still goes through. Run against the real disk, where the bytes actually land.
        const int rows = 50_000;
        var target = _host.File("large.xlsx");
        var readBack = new CollectingSink<Order>();

        _host.AddEtlPipeline(Write, b => b
                 .WithOptions(o => o.BatchSize = 1_000)
                 .From(new ArraySource<Order>(Orders(rows)))
                 .ToExcel(target))
             .AddEtlPipeline(Read, b => b
                 .WithOptions(o => o.BatchSize = 1_000)
                 .FromExcel<Order>(target)
                 .To(_ => readBack));

        //act
        var write = await _host.RunAsync(Write);
        var read = await _host.RunAsync(Read);

        //assert
        write.IsError.Should().BeFalse(write.IsError ? write.FirstError.Description : null);
        read.IsError.Should().BeFalse(read.IsError ? read.FirstError.Description : null);

        write.Value.RowsWritten.Should().Be(rows);
        readBack.Rows.Should().HaveCount(rows);
        readBack.Rows[^1].Id.Should().Be(rows - 1, "the last row survives the whole trip");
    }
}
