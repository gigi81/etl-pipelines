using EtlPipelines.Abstractions.Building;
using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Tests;

/// <summary>
/// Bad-row tolerance: the ETL concern that <c>ErrorOr&lt;int&gt;</c> on a batch cannot express on its
/// own, since it says the batch failed without saying which row or what to do about it.
/// </summary>
public class RowErrorPolicyTests
{
    /// <summary>Rejects every multiple of ten, so failures are spread across many batches.</summary>
    private static ErrorOr<int> RejectMultiplesOfTen(int value) =>
        value % 10 == 0
            ? Error.Validation("row.rejected", $"Rejected {value}.")
            : value;

    private static IPipelineBuilder Pipeline(InMemorySink<int> sink, Action<PipelineOptions> options) =>
        EtlPipeline.CreateBuilder("errors")
            .WithOptions(o =>
            {
                o.BatchSize = 8;
                options(o);
            })
            .From(new InMemorySource<int>(Enumerable.Range(1, 100)))
            .TrySelect(RejectMultiplesOfTen)
            .To(sink);

    [Fact]
    public async Task Fails_the_run_on_the_first_bad_row_by_default()
    {
        var sink = new InMemorySink<int>();
        var result = await Pipeline(sink, _ => { }).Build().RunAsync(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("row.rejected");
        sink.Completions.Should().Be(0, "a failed run must not commit");
    }

    [Fact]
    public async Task Skips_bad_rows_and_finishes_when_told_to()
    {
        var sink = new InMemorySink<int>();

        var result = await Pipeline(sink, o => o.OnRowError = RowErrorAction.Skip)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // Ten multiples of ten in 1..100 are dropped; the surrounding rows still flow, which is the
        // point — a rejected row must not cost the rest of its batch.
        result.Value.RowsFailed.Should().Be(10);
        sink.Rows.Should().HaveCount(90);
        sink.Rows.Should().NotContain(x => x % 10 == 0);
        sink.Completions.Should().Be(1);
    }

    [Fact]
    public async Task Routes_bad_rows_to_the_dead_letter_sink()
    {
        var deadLetters = new RecordingDeadLetterSink<int>();
        var sink = new InMemorySink<int>();

        var services = new ServiceCollection();
        services.AddSingleton<IDataSource<int>>(new InMemorySource<int>(Enumerable.Range(1, 100)));
        services.AddSingleton<IDeadLetterSink<int>>(deadLetters);
        services.AddSingleton<IDataSink<int>>(sink);

        services.AddEtlPipeline("errors", builder => builder
            .WithOptions(o =>
            {
                o.BatchSize = 8;
                o.OnRowError = RowErrorAction.DeadLetter;
            })
            .From<int>()
            .TrySelect(RejectMultiplesOfTen)
            .To(sink));

        var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IPipelineFactory>().Get("errors")
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsFailed.Should().Be(10);

        // The rejected rows are recoverable, not merely counted.
        deadLetters.Entries.Should().HaveCount(10);
        deadLetters.Entries.Select(e => e.Row).Should().OnlyContain(x => x % 10 == 0);
        deadLetters.Entries.Should().OnlyContain(e => e.Error.Code == "row.rejected");
    }

    [Fact]
    public async Task Stops_once_too_many_rows_have_been_rejected()
    {
        var sink = new InMemorySink<int>();

        var result = await Pipeline(sink, o =>
            {
                o.OnRowError = RowErrorAction.Skip;
                o.MaxRowErrors = 3;
            })
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeTrue("tolerance is not the same as ignoring failures entirely");
        result.FirstError.Code.Should().Be("pipeline.too_many_row_errors");
        result.FirstError.Description.Should().Contain("limit 3");
    }

    [Fact]
    public async Task Treats_zero_max_errors_as_unlimited()
    {
        var sink = new InMemorySink<int>();

        var result = await Pipeline(sink, o =>
            {
                o.OnRowError = RowErrorAction.Skip;
                o.MaxRowErrors = 0;
            })
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Value.RowsFailed.Should().Be(10);
    }

    [Fact]
    public async Task Reports_a_sink_failure_against_the_whole_batch()
    {
        // A sink rejects a batch, not a row, so the policy applies at batch granularity there. The
        // count reflects that honestly rather than pretending one row was at fault.
        var sink = new InMemorySink<int> { Reject = x => x == 42 };

        var result = await EtlPipeline.CreateBuilder("errors")
            .WithOptions(o =>
            {
                o.BatchSize = 8;
                o.OnRowError = RowErrorAction.Skip;
            })
            .From(new InMemorySource<int>(Enumerable.Range(1, 100)))
            .To(sink)
            .Build()
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Value.RowsFailed.Should().Be(8, "the batch containing the bad row is what the sink refused");
        sink.Rows.Should().HaveCount(92);
    }

    [Fact]
    public void Rejects_options_that_cannot_produce_a_working_pipeline()
    {
        var act = () => EtlPipeline.CreateBuilder("bad").WithOptions(o => o.BatchSize = 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
