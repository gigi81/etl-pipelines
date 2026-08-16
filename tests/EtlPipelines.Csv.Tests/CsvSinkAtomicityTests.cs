using EtlPipelines.Csv;
using FluentAssertions;

namespace EtlPipelines.Csv.Tests;

/// <summary>
/// The target file must never be seen half-written.
/// </summary>
/// <remarks>
/// A sink streams batches to disk as they arrive, so without care a run that dies part-way leaves a
/// truncated file at the real path — and a downstream job consuming it cannot tell the difference.
/// </remarks>
public sealed class CsvSinkAtomicityTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etl-csv-atomic-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    private sealed record Row(int Id, string Name);

    private static Row[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Row(i, $"n{i}"))];

    [Fact]
    public async Task The_target_appears_only_once_the_run_succeeds()
    {
        var target = Path("ok.csv");

        var result = await EtlPipeline.CreateBuilder("atomic")
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(100)))
            .ToCsv(target)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        File.Exists(target).Should().BeTrue();
        (await File.ReadAllLinesAsync(target, CancellationToken.None)).Should().HaveCount(101, "header plus 100 rows");
        TempFiles().Should().BeEmpty("the temporary file is renamed, not left behind, on success");
    }

    [Fact]
    public async Task A_failed_run_leaves_no_target_file_at_all()
    {
        // The whole point: a consumer watching for ok.csv must never see a truncated one.
        var target = Path("failed.csv");

        // The sibling fails slowly and part-way, so the CSV branch provably has rows on disk by then.
        // Back-pressure keeps it from running away and finishing the whole file first.
        var result = await EtlPipeline.CreateBuilder("atomic")
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(400)))
            .Branch(
                b1 => b1.ToCsv(target),
                b2 => b2.To(new FailingSink<Row>(failAfter: 40, delayPerBatch: TimeSpan.FromMilliseconds(10))))
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeTrue();

        File.Exists(target).Should().BeFalse("a partial file must never be promoted to the target path");

        var temps = TempFiles();
        temps.Should().ContainSingle("the partial write is kept for inspection rather than deleted");

        // Without this the test would also pass had the sink failed before writing anything, which is
        // a much weaker claim than "a genuinely partial file was never promoted".
        var written = await File.ReadAllLinesAsync(temps[0], CancellationToken.None);
        written.Length.Should().BeGreaterThan(1, "rows had already streamed to disk when the run failed");
        written.Length.Should().BeLessThan(401, "and the file is genuinely incomplete");
    }

    [Fact]
    public async Task A_failed_run_does_not_disturb_a_previous_good_file()
    {
        var target = Path("existing.csv");
        await File.WriteAllTextAsync(target, "Id,Name\n999,previous\n", CancellationToken.None);

        var result = await EtlPipeline.CreateBuilder("atomic")
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(100)))
            .Branch(
                b1 => b1.ToCsv(target),
                b2 => b2.To(new FailingSink<Row>(failAfter: 16)))
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeTrue();

        var lines = await File.ReadAllLinesAsync(target, CancellationToken.None);
        lines.Should().Equal("Id,Name", "999,previous");
    }

    [Fact]
    public async Task Writes_straight_to_the_target_when_atomicity_is_turned_off()
    {
        var target = Path("direct.csv");

        var result = await EtlPipeline.CreateBuilder("direct")
            .From(new ArraySource<Row>(Rows(10)))
            .ToCsv(target, new CsvSinkOptions { WriteAtomically = false })
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse();
        File.Exists(target).Should().BeTrue();
        TempFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task Creates_the_destination_directory()
    {
        var target = Path(System.IO.Path.Combine("nested", "deeper", "out.csv"));

        var result = await EtlPipeline.CreateBuilder("nested")
            .From(new ArraySource<Row>(Rows(3)))
            .ToCsv(target)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        File.Exists(target).Should().BeTrue();
    }

    [Fact]
    public async Task Writes_to_a_supplied_TextWriter_without_renaming()
    {
        var buffer = new StringWriter();

        var result = await EtlPipeline.CreateBuilder("writer")
            .From(new ArraySource<Row>(Rows(3)))
            .To(new CsvSink<Row>(buffer, leaveOpen: true))
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        buffer.ToString().Should().Contain("Id,Name").And.Contain("0,n0");
    }

    private string[] TempFiles() => Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories);
}
