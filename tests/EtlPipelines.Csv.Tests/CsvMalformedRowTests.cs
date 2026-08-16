using System.IO.Abstractions;
using EtlPipelines.Abstractions.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
public sealed class CsvMalformedRowTests
{
    private readonly CsvTestHost _host = new();

    public sealed record Order(int Id, string Customer, decimal Amount);

    /// <summary>Writes a file whose 2nd, 5th and 8th data rows have an unparseable amount.</summary>
    private async Task<IFileInfo> FileWithBadRows()
    {
        var lines = new List<string> { "Id,Customer,Amount" };

        for (var i = 1; i <= 10; i++)
        {
            lines.Add(i is 2 or 5 or 8
                ? $"{i},customer-{i},not-a-number"
                : $"{i},customer-{i},{i}.50");
        }

        var file = _host.File("bad.csv");
        await file.WriteAllLinesAsync(lines, CancellationToken.None);
        return file;
    }

    [Fact]
    public async Task Skips_bad_rows_and_keeps_the_good_ones()
    {
        var file = await FileWithBadRows();
        var sink = new CollectingSink<Order>();

        _host.AddPipeline("bad", b => b
            .WithOptions(o => o.BatchSize = 4)
            .FromCsv<Order>(file)
            .To(sink));

        var result = await _host.RunAsync("bad");

        result.IsError.Should().BeFalse("three bad rows must not cost the other seven");
        sink.Rows.Should().HaveCount(7);
        sink.Rows.Select(o => o.Id).Should().Equal(1, 3, 4, 6, 7, 9, 10);
    }

    [Fact]
    public async Task Takes_the_dead_letter_sink_from_the_container()
    {
        // Nothing wires the dead-letter sink to the source explicitly: FromCsv looks for one in the
        // container. Registering it is the whole of the configuration.
        var file = await FileWithBadRows();
        var deadLetters = new RecordingDeadLetterSink<string>();
        var sink = new CollectingSink<Order>();

        _host.Configure(s => s.AddSingleton<IDeadLetterSink<string>>(deadLetters))
             .AddPipeline("bad", b => b.FromCsv<Order>(file).To(sink));

        await _host.RunAsync("bad");

        // Raw text is what makes the row recoverable — the parsed shape is exactly what is missing.
        deadLetters.Entries.Should().HaveCount(3);
        deadLetters.Entries.Select(e => e.Row.Trim())
            .Should().Equal("2,customer-2,not-a-number", "5,customer-5,not-a-number", "8,customer-8,not-a-number");
        deadLetters.Entries.Should().OnlyContain(e => e.Error.Code == "csv.malformed_row");
    }

    [Fact]
    public async Task Counts_skipped_rows_without_a_dead_letter_sink_registered()
    {
        var file = await FileWithBadRows();
        var source = new CsvSource<Order>(file);

        _host.AddPipeline("bad", b => b.From<Order>(_ => source).To(new CollectingSink<Order>()));

        await _host.RunAsync("bad");

        source.MalformedRows.Should().Be(3, "skipping is not the same as ignoring");
    }

    [Fact]
    public async Task Skipped_rows_do_not_reach_the_runs_failure_count()
    {
        // Pinning a known limitation rather than asserting desired behaviour. A transform rejecting a
        // row flows into RowsFailed and MaxRowErrors; IDataSource.ReadAsync returns only a count, so
        // a source has nowhere to report one. Closing the gap means giving the source port a
        // rejection channel — when that lands, this test should change.
        var file = await FileWithBadRows();
        var source = new CsvSource<Order>(file);

        _host.AddPipeline("bad", b => b.From<Order>(_ => source).To(new CollectingSink<Order>()));

        var result = await _host.RunAsync("bad");

        source.MalformedRows.Should().Be(3, "the source counts them itself");
        result.Value.RowsFailed.Should().Be(0, "but the source port cannot report them to the run");
    }

    [Fact]
    public async Task Fails_the_run_when_told_not_to_skip()
    {
        var file = await FileWithBadRows();

        _host.AddPipeline("strict", b => b
            .FromCsv<Order>(file, new CsvSourceOptions { SkipMalformedRows = false })
            .To(new CollectingSink<Order>()));

        var result = await _host.RunAsync("strict");

        result.IsError.Should().BeTrue("a file meant to be perfect should stop the run when it is not");
    }

    [Fact]
    public async Task Reports_a_clear_error_when_read_before_initialization()
    {
        var file = await FileWithBadRows();
        var source = new CsvSource<Order>(file);

        var read = await source.ReadAsync(new Order[1], CancellationToken.None);

        read.IsError.Should().BeTrue();
        read.FirstError.Code.Should().Be("csv.not_initialized");
    }
}
