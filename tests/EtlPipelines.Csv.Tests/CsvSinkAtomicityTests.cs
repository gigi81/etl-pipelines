using System.IO.Abstractions;
using FluentAssertions;

namespace EtlPipelines.Csv.Tests;

/// <summary>
/// The target file must never be seen half-written.
/// </summary>
/// <remarks>
/// A sink streams batches to disk as they arrive, so without care a run that dies part-way leaves a
/// truncated file at the real path — and a downstream job consuming it cannot tell the difference.
/// </remarks>
public sealed class CsvSinkAtomicityTests
{
    private readonly CsvTestHost _host = new();

    private sealed record Row(int Id, string Name);

    private static Row[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Row(i, $"n{i}"))];

    [Fact]
    public async Task The_target_appears_only_once_the_run_succeeds()
    {
        var target = _host.File("ok.csv");

        _host.AddPipeline("atomic", b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(100)))
            .ToCsv(target));

        var result = await _host.RunAsync("atomic");

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        (await target.ReadAllLinesAsync(CancellationToken.None))
            .Should().HaveCount(101, "header plus 100 rows");
        _host.TempFiles().Should().BeEmpty("the temporary file is renamed, not left behind, on success");
    }

    [Fact]
    public async Task A_failed_run_leaves_no_target_file_at_all()
    {
        var target = _host.File("failed.csv");

        // The sibling fails slowly and part-way, so the CSV branch provably has rows on disk by then.
        // Back-pressure keeps it from running away and finishing the whole file first.
        _host.AddPipeline("atomic", b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(400)))
            .Branch(
                b1 => b1.ToCsv(target),
                b2 => b2.To(new FailingSink<Row>(failAfter: 40, delayPerBatch: TimeSpan.FromMilliseconds(10)))));

        var result = await _host.RunAsync("atomic");

        result.IsError.Should().BeTrue();

        target.Refresh();
        target.Exists.Should().BeFalse("a partial file must never be promoted to the target path");

        var temps = _host.TempFiles();
        temps.Should().ContainSingle("the partial write is kept for inspection rather than deleted");

        // Without this the test would also pass had the sink failed before writing anything, which is
        // a much weaker claim than "a genuinely partial file was never promoted".
        var written = await temps[0].ReadAllLinesAsync(CancellationToken.None);
        written.Length.Should().BeGreaterThan(1, "rows had already streamed to disk when the run failed");
        written.Length.Should().BeLessThan(401, "and the file is genuinely incomplete");
    }

    [Fact]
    public async Task A_failed_run_does_not_disturb_a_previous_good_file()
    {
        var target = _host.File("existing.csv");
        await target.WriteAllTextAsync("Id,Name\n999,previous\n", CancellationToken.None);

        _host.AddPipeline("atomic", b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(400)))
            .Branch(
                b1 => b1.ToCsv(target),
                b2 => b2.To(new FailingSink<Row>(failAfter: 40, delayPerBatch: TimeSpan.FromMilliseconds(10)))));

        var result = await _host.RunAsync("atomic");

        result.IsError.Should().BeTrue();
        (await target.ReadAllLinesAsync(CancellationToken.None)).Should().Equal("Id,Name", "999,previous");
    }

    [Fact]
    public async Task Writes_straight_to_the_target_when_atomicity_is_turned_off()
    {
        var target = _host.File("direct.csv");

        _host.AddPipeline("direct", b => b
            .From(new ArraySource<Row>(Rows(10)))
            .ToCsv(target, new CsvSinkOptions { WriteAtomically = false }));

        var result = await _host.RunAsync("direct");

        result.IsError.Should().BeFalse();
        target.Refresh();
        target.Exists.Should().BeTrue();
        _host.TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Creates_the_destination_directory()
    {
        // Reads as a path walk rather than string concatenation, and neither directory exists yet.
        var target = _host.Root.SubDirectory("nested", "deeper").File("out.csv");

        _host.AddPipeline("nested", b => b.From(new ArraySource<Row>(Rows(3))).ToCsv(target));

        var result = await _host.RunAsync("nested");

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // IFileInfo caches Exists from when it was created, which was before the sink ran.
        target.Refresh();
        target.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task Writes_to_a_supplied_TextWriter_without_renaming()
    {
        var buffer = new StringWriter();

        _host.AddPipeline("writer", b => b
            .From(new ArraySource<Row>(Rows(3)))
            .To(new CsvSink<Row>(buffer, leaveOpen: true)));

        var result = await _host.RunAsync("writer");

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        buffer.ToString().Should().Contain("Id,Name").And.Contain("0,n0");
    }
}
