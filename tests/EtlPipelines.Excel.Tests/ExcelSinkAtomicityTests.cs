using System.IO.Abstractions;

namespace EtlPipelines.Excel.Tests;

/// <summary>
/// The target workbook must never be seen half-written.
/// </summary>
/// <remarks>
/// This matters more for a workbook than for a CSV. An <c>.xlsx</c> is a zip archive whose central
/// directory is written last, so a run that dies part-way leaves not a short-but-readable file but one
/// Excel refuses to open at all — and it would sit at the real path for a downstream job to find.
/// </remarks>
public sealed class ExcelSinkAtomicityTests
{
    private const string PipelineName = "atomic";

    private readonly ExcelTestHost _host = new();

    public sealed class Row
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private static Row[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Row { Id = i, Name = $"n{i}" })];

    /// <summary>A sink that fails part-way and slowly, so a sibling branch provably reaches disk first.</summary>
    private static FailingSink<Row> SlowFailure() =>
        new(failAfter: 40, delayPerBatch: TimeSpan.FromMilliseconds(10));

    [Test]
    public async Task The_target_appears_only_once_the_run_succeeds()
    {
        //arrange
        var target = _host.File("ok.xlsx");
        var readBack = new CollectingSink<Row>();

        _host.AddEtlPipeline(PipelineName, b => b
                 .WithOptions(o => o.BatchSize = 8)
                 .From(new ArraySource<Row>(Rows(100)))
                 .ToExcel(target))
             .AddEtlPipeline("read", b => b.FromExcel<Row>(target).To(_ => readBack));

        //act
        var result = await _host.RunAsync(PipelineName);
        await _host.RunAsync("read");

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        readBack.Rows.Should().HaveCount(100, "the promoted workbook opens and holds every row");
        _host.TempFiles().Should().BeEmpty("the temporary file is renamed, not left behind, on success");
    }

    [Test]
    public async Task A_failed_run_leaves_no_target_file_at_all()
    {
        //arrange
        // Back-pressure keeps the workbook branch from running away and finishing the whole file
        // before the sibling fails, so the write really is partial when the run gives up.
        var target = _host.File("failed.xlsx");

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(400)))
            .Branch(
                b1 => b1.ToExcel(target),
                b2 => b2.To(SlowFailure())));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();

        target.Refresh();
        target.Exists.Should().BeFalse("a partial workbook must never be promoted to the target path");

        var temps = _host.TempFiles();
        temps.Should().ContainSingle("the partial write is kept for inspection rather than deleted");

        // Without this the test would also pass had the sink failed before writing anything, which is
        // a much weaker claim than "a genuinely partial file was never promoted".
        temps[0].Refresh();
        temps[0].Length.Should().BeGreaterThan(0, "bytes had already streamed to disk when the run failed");
    }

    [Test]
    public async Task A_failed_run_does_not_disturb_a_previous_good_file()
    {
        //arrange
        var target = _host.File("existing.xlsx");
        var previous = new ExcelSink<Row>(target);

        await previous.InitializeAsync(CancellationToken.None);
        await previous.WriteAsync(new[] { new Row { Id = 999, Name = "previous" } }, CancellationToken.None);
        await previous.CompleteAsync(CancellationToken.None);
        await previous.DisposeAsync();

        var readBack = new CollectingSink<Row>();

        _host.AddEtlPipeline(PipelineName, b => b
                 .WithOptions(o => o.BatchSize = 8)
                 .From(new ArraySource<Row>(Rows(400)))
                 .Branch(
                     b1 => b1.ToExcel(target),
                     b2 => b2.To(SlowFailure())))
             .AddEtlPipeline("read", b => b.FromExcel<Row>(target).To(_ => readBack));

        //act
        var result = await _host.RunAsync(PipelineName);
        await _host.RunAsync("read");

        //assert
        result.IsError.Should().BeTrue();
        readBack.Rows.Should().ContainSingle("the good workbook that was already there is untouched");
        readBack.Rows[0].Name.Should().Be("previous");
    }

    [Test]
    public async Task Writes_straight_to_the_target_when_atomicity_is_turned_off()
    {
        //arrange
        var target = _host.File("direct.xlsx");

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(50)))
            .ToExcel(target, new ExcelSinkOptions { WriteAtomically = false }));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        target.Refresh();
        target.Exists.Should().BeTrue();
        _host.TempFiles().Should().BeEmpty("nothing is written through a temporary file in this mode");
    }

    [Test]
    public async Task Creates_the_destination_directory()
    {
        //arrange
        var target = _host.Root.SubDirectory("nested", "deeper").File("made.xlsx");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Row>(Rows(5)))
            .ToExcel(target));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        target.Refresh();
        target.Exists.Should().BeTrue("a destination directory that does not exist yet is created");
    }

    [Test]
    public async Task Writes_to_a_supplied_stream_without_renaming()
    {
        //arrange
        // No file, so there is nothing to rename and the atomic behaviour does not apply. The workbook
        // is still complete, because the writer is finished on completion.
        using var buffer = new MemoryStream();
        var sink = new ExcelSink<Row>(buffer, leaveOpen: true);

        //act
        await sink.InitializeAsync(CancellationToken.None);
        var written = await sink.WriteAsync(Rows(20), CancellationToken.None);
        var completed = await sink.CompleteAsync(CancellationToken.None);
        await sink.DisposeAsync();

        //assert
        written.IsError.Should().BeFalse();
        written.Value.Should().Be(20);
        completed.IsError.Should().BeFalse(completed.IsError ? completed.FirstError.Description : null);
        buffer.Length.Should().BeGreaterThan(0, "the workbook was built into the stream it was given");
        _host.TempFiles().Should().BeEmpty();
    }
}
