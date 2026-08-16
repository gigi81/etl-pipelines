using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Csv;
using FluentAssertions;

namespace EtlPipelines.Csv.Tests;

/// <summary>
/// What happens to rows a CSV file cannot parse.
/// </summary>
/// <remarks>
/// One unparseable row costing a ten-million-row load is the classic CSV complaint, so the source
/// skips them by default, counts them, and hands the raw text to a dead-letter sink when one is
/// registered. These tests also pin the known limitation: the source port has no per-row rejection
/// channel, so those skips cannot reach the run's <c>RowsFailed</c>.
/// </remarks>
public sealed class CsvMalformedRowTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("etl-csv-bad-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public sealed record Order(int Id, string Customer, decimal Amount);

    /// <summary>Writes a file whose 2nd, 5th and 8th data rows have an unparseable amount.</summary>
    private async Task<string> FileWithBadRows()
    {
        var path = System.IO.Path.Combine(_directory, "bad.csv");
        var lines = new List<string> { "Id,Customer,Amount" };

        for (var i = 1; i <= 10; i++)
        {
            lines.Add(i is 2 or 5 or 8
                ? $"{i},customer-{i},not-a-number"
                : $"{i},customer-{i},{i}.50");
        }

        await File.WriteAllLinesAsync(path, lines, CancellationToken.None);
        return path;
    }

    [Fact]
    public async Task Skips_bad_rows_and_keeps_the_good_ones()
    {
        var path = await FileWithBadRows();
        var sink = new CollectingSink<Order>();

        var result = await EtlPipeline.CreateBuilder("bad")
            .WithOptions(o => o.BatchSize = 4)
            .FromCsv<Order>(path)
            .To(sink)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse("three bad rows must not cost the other seven");
        sink.Rows.Should().HaveCount(7);
        sink.Rows.Select(o => o.Id).Should().Equal(1, 3, 4, 6, 7, 9, 10);
    }

    [Fact]
    public async Task Hands_the_raw_text_of_a_bad_row_to_the_dead_letter_sink()
    {
        var path = await FileWithBadRows();
        var deadLetters = new RecordingDeadLetterSink<string>();
        var source = new CsvSource<Order>(path, deadLetters: deadLetters);

        var sink = new CollectingSink<Order>();
        await EtlPipeline.CreateBuilder("bad")
            .From<Order>(_ => source)
            .To(sink)
            .Build()
            .RunAsync(CancellationToken.None);

        source.MalformedRows.Should().Be(3);

        // Raw text is what makes the row recoverable — the parsed shape is exactly what is missing.
        deadLetters.Entries.Should().HaveCount(3);
        deadLetters.Entries.Select(e => e.Row.Trim())
            .Should().Equal("2,customer-2,not-a-number", "5,customer-5,not-a-number", "8,customer-8,not-a-number");
        deadLetters.Entries.Should().OnlyContain(e => e.Error.Code == "csv.malformed_row");
    }

    [Fact]
    public async Task Skipped_rows_do_not_reach_the_runs_failure_count()
    {
        // Pinning a known limitation rather than asserting desired behaviour. A transform rejecting a
        // row flows into RowsFailed and MaxRowErrors; IDataSource.ReadAsync returns only a count, so
        // a source has nowhere to report one. Closing the gap means giving the source port a
        // rejection channel — when that lands, this test should change.
        var path = await FileWithBadRows();
        var source = new CsvSource<Order>(path);

        var result = await EtlPipeline.CreateBuilder("bad")
            .From<Order>(_ => source)
            .To(new CollectingSink<Order>())
            .Build()
            .RunAsync(CancellationToken.None);

        source.MalformedRows.Should().Be(3, "the source counts them itself");
        result.Value.RowsFailed.Should().Be(0, "but the source port cannot report them to the run");
    }

    [Fact]
    public async Task Fails_the_run_when_told_not_to_skip()
    {
        var path = await FileWithBadRows();

        var result = await EtlPipeline.CreateBuilder("strict")
            .FromCsv<Order>(path, new CsvSourceOptions { SkipMalformedRows = false })
            .To(new CollectingSink<Order>())
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeTrue("a file meant to be perfect should stop the run when it is not");
    }

    [Fact]
    public async Task Reports_a_clear_error_when_read_before_initialization()
    {
        var path = await FileWithBadRows();
        var source = new CsvSource<Order>(path);

        var read = await source.ReadAsync(new Order[1], CancellationToken.None);

        read.IsError.Should().BeTrue();
        read.FirstError.Code.Should().Be("csv.not_initialized");
    }
}
