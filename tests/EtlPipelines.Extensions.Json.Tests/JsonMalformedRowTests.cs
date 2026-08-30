using System.IO.Abstractions;
using EtlPipelines.Abstractions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Extensions.Json.Tests;

/// <summary>
/// What happens to rows a JSON file cannot parse.
/// </summary>
/// <remarks>
/// Under <see cref="JsonFormat.Lines"/> a line is a recovery boundary, so - like the CSV extension -
/// the source skips a bad one by default, counts it, and hands the raw text to a dead-letter sink when
/// one is registered. <see cref="JsonFormat.Array"/> has no such boundary inside the array, so a
/// malformed element there always fails the run; see <see cref="Array_format_has_no_row_level_recovery"/>.
/// </remarks>
public sealed class JsonMalformedRowTests
{
    private const string PipelineName = "orders";

    private readonly JsonTestHost _host = new();

    public sealed record Order(int Id, string Customer, decimal Amount);

    /// <summary>Writes a JSON Lines file whose 2nd, 5th and 8th data rows are not valid JSON.</summary>
    private async Task<IFileInfo> FileWithBadRows()
    {
        var lines = new List<string>();

        for (var i = 1; i <= 10; i++)
        {
            lines.Add(i is 2 or 5 or 8
                ? $"customer-{i} has no braces at all"
                : $$"""{"id":{{i}},"customer":"customer-{{i}}","amount":{{i}}.50}""");
        }

        var file = _host.File("bad.json");
        await file.WriteAllLinesAsync(lines, CancellationToken.None);
        return file;
    }

    [Test]
    public async Task Skips_bad_lines_and_keeps_the_good_ones()
    {
        //arrange
        var file = await FileWithBadRows();
        var sink = new CollectingSink<Order>();

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 4)
            .FromJson<Order>(file)
            .To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse("three bad lines must not cost the other seven");
        sink.Rows.Should().HaveCount(7);
        sink.Rows.Select(o => o.Id).Should().Equal(1, 3, 4, 6, 7, 9, 10);
    }

    [Test]
    public async Task Takes_the_dead_letter_sink_from_the_container()
    {
        //arrange
        // Nothing wires the dead-letter sink to the source explicitly: FromJson looks for one in the
        // container. Registering it is the whole of the configuration.
        var file = await FileWithBadRows();
        var deadLetters = new RecordingDeadLetterSink<string>();

        _host.Configure(s => s.AddSingleton<IDeadLetterSink<string>>(deadLetters))
             .AddEtlPipeline(PipelineName, b => b.FromJson<Order>(file).To(new CollectingSink<Order>()));

        //act
        await _host.RunAsync(PipelineName);

        //assert
        // Raw text is what makes the row recoverable — the parsed shape is exactly what is missing.
        deadLetters.Entries.Should().HaveCount(3);
        deadLetters.Entries.Select(e => e.Row)
            .Should().Equal(
                "customer-2 has no braces at all",
                "customer-5 has no braces at all",
                "customer-8 has no braces at all");
        deadLetters.Entries.Should().OnlyContain(e => e.Error.Code == "json.malformed_row");
    }

    [Test]
    public async Task Counts_skipped_rows_without_a_dead_letter_sink_registered()
    {
        //arrange
        var file = await FileWithBadRows();
        var source = new JsonSource<Order>(file);

        _host.AddEtlPipeline(PipelineName, b => b.From<Order>(_ => source).To(new CollectingSink<Order>()));

        //act
        await _host.RunAsync(PipelineName);

        //assert
        source.MalformedRows.Should().Be(3, "skipping is not the same as ignoring");
    }

    [Test]
    public async Task Skipped_rows_do_not_reach_the_runs_failure_count()
    {
        //arrange
        // Pinning a known limitation rather than asserting desired behaviour — the same one the CSV
        // extension pins. A transform rejecting a row flows into RowsFailed and MaxRowErrors;
        // IDataSource.ReadAsync returns only a count, so a source has nowhere to report one.
        var file = await FileWithBadRows();
        var source = new JsonSource<Order>(file);

        _host.AddEtlPipeline(PipelineName, b => b.From<Order>(_ => source).To(new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        source.MalformedRows.Should().Be(3, "the source counts them itself");
        result.Value.RowsFailed.Should().Be(0, "but the source port cannot report them to the run");
    }

    [Test]
    public async Task Fails_the_run_when_told_not_to_skip()
    {
        //arrange
        var file = await FileWithBadRows();

        _host.AddEtlPipeline(PipelineName, b => b
            .FromJson<Order>(file, new JsonSourceOptions { SkipMalformedRows = false })
            .To(new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue("a file meant to be perfect should stop the run when it is not");
    }

    [Test]
    public async Task Array_format_has_no_row_level_recovery()
    {
        //arrange
        // A JSON array with one malformed element part-way through. Unlike a line, there is no
        // boundary to skip to inside an array - the reader's position is simply lost - so this fails
        // the whole read even though SkipMalformedRows stays at its default of true.
        var file = _host.File("bad-array.json");
        await file.WriteAllTextAsync(
            """[{"id":1,"customer":"acme","amount":1.50},{"id":2,not valid json},{"id":3,"customer":"c","amount":3.50}]""",
            CancellationToken.None);

        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJson<Order>(file, new JsonSourceOptions { Format = JsonFormat.Array })
                  .To(new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue("an array has no per-element recovery boundary to skip to");
    }

    [Test]
    public async Task Reports_a_clear_error_when_read_before_initialization()
    {
        //arrange
        var file = await FileWithBadRows();
        var source = new JsonSource<Order>(file);

        //act
        var read = await source.ReadAsync(new Order[1], CancellationToken.None);

        //assert
        read.IsError.Should().BeTrue();
        read.FirstError.Code.Should().Be("json.not_initialized");
    }
}
