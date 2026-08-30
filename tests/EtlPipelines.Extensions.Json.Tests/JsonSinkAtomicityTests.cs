using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Json.Tests;

/// <summary>
/// The target file must never be seen half-written.
/// </summary>
/// <remarks>
/// A sink streams batches to disk as they arrive, so without care a run that dies part-way leaves a
/// truncated file at the real path — and a downstream job consuming it cannot tell the difference. For
/// <see cref="JsonFormat.Array"/> a truncated file is also simply not valid JSON, since the closing
/// <c>]</c> is only ever written once the run has succeeded.
/// </remarks>
public sealed class JsonSinkAtomicityTests
{
    private const string PipelineName = "atomic";

    private readonly JsonTestHost _host = new();

    private sealed record Row(int Id, string Name);

    private static Row[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Row(i, $"n{i}"))];

    /// <summary>A sink that fails part-way and slowly, so a sibling branch provably reaches disk first.</summary>
    private static FailingSink<Row> SlowFailure() =>
        new(failAfter: 40, delayPerBatch: TimeSpan.FromMilliseconds(10));

    [Test]
    public async Task The_target_appears_only_once_the_run_succeeds()
    {
        //arrange
        var target = _host.File("ok.json");

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(100)))
            .ToJson(target));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await target.ReadAllLinesAsync(CancellationToken.None)).Should().HaveCount(100);
        _host.TempFiles().Should().BeEmpty("the temporary file is renamed, not left behind, on success");
    }

    [Test]
    public async Task A_failed_run_leaves_no_target_file_at_all()
    {
        //arrange
        // Back-pressure keeps the JSON branch from running away and finishing the whole file before
        // the sibling fails, so the write really is partial when the run gives up.
        var target = _host.File("failed.json");

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(400)))
            .Branch(
                b1 => b1.ToJson(target),
                b2 => b2.To(SlowFailure())));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();

        target.Refresh();
        target.Exists.Should().BeFalse("a partial file must never be promoted to the target path");

        var temps = _host.TempFiles();
        temps.Should().ContainSingle("the partial write is kept for inspection rather than deleted");

        // Without this the test would also pass had the sink failed before writing anything, which is
        // a much weaker claim than "a genuinely partial file was never promoted".
        var written = await temps[0].ReadAllLinesAsync(CancellationToken.None);
        written.Length.Should().BeGreaterThan(1, "rows had already streamed to disk when the run failed");
        written.Length.Should().BeLessThan(400, "and the file is genuinely incomplete");
    }

    [Test]
    public async Task A_failed_run_under_Array_format_leaves_an_unterminated_temp_file()
    {
        //arrange
        var target = _host.File("failed-array.json");

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(400)))
            .Branch(
                b1 => b1.ToJson(target, new JsonSinkOptions { Format = JsonFormat.Array }),
                b2 => b2.To(SlowFailure())));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();

        var temps = _host.TempFiles();
        temps.Should().ContainSingle();

        var written = await temps[0].ReadAllTextAsync(CancellationToken.None);
        written.Should().StartWith("[", "rows had already streamed to disk when the run failed");
        written.Should().NotEndWith("]", "CompleteAsync, which writes the closing bracket, never ran");
    }

    [Test]
    public async Task A_failed_run_does_not_disturb_a_previous_good_file()
    {
        //arrange
        var target = _host.File("existing.json");
        await target.WriteAllTextAsync("{\"id\":999,\"name\":\"previous\"}\n", CancellationToken.None);

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Row>(Rows(400)))
            .Branch(
                b1 => b1.ToJson(target),
                b2 => b2.To(SlowFailure())));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();
        (await target.ReadAllLinesAsync(CancellationToken.None))
            .Should().Equal("{\"id\":999,\"name\":\"previous\"}");
    }

    [Test]
    public async Task Writes_straight_to_the_target_when_atomicity_is_turned_off()
    {
        //arrange
        var target = _host.File("direct.json");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Row>(Rows(10)))
            .ToJson(target, new JsonSinkOptions { WriteAtomically = false }));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        target.Refresh();
        target.Exists.Should().BeTrue();
        _host.TempFiles().Should().BeEmpty();
    }

    [Test]
    public async Task Creates_the_destination_directory()
    {
        //arrange
        // Reads as a path walk rather than string concatenation, and neither directory exists yet.
        var target = _host.Root.SubDirectory("nested", "deeper").File("out.json");

        _host.AddEtlPipeline(PipelineName, b => b.From(new ArraySource<Row>(Rows(3))).ToJson(target));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // IFileInfo caches Exists from when it was created, which was before the sink ran.
        target.Refresh();
        target.Exists.Should().BeTrue();
    }

    [Test]
    public async Task Writes_to_a_supplied_stream_without_renaming()
    {
        //arrange
        var buffer = new MemoryStream();

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Row>(Rows(3)))
            .To(new JsonSink<Row>(buffer, leaveOpen: true)));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        var written = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        written.Should().Contain("\"id\":0").And.Contain("\"name\":\"n0\"");
    }
}
