using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Tests.Fixtures;
using FluentAssertions;
using System.Diagnostics;

namespace EtlPipelines.Tests;

/// <summary>
/// Properties of the runtime itself: back-pressure, overlap, pooling and cancellation.
/// </summary>
/// <remarks>
/// These are the claims the redesign was made for. If extract and load do not actually overlap, the
/// channel wiring has been serialized somewhere and the main reason for replacing an opaque
/// <c>Execute()</c> with typed ports has quietly been lost — so it is asserted rather than assumed.
/// </remarks>
public class DataflowRuntimeTests
{
    /// <summary>A struct row, so buffer pooling is observable without per-row object allocation.</summary>
    private readonly record struct Measurement(long Id, double Value);

    [Fact]
    public async Task Back_pressure_bounds_how_far_the_source_runs_ahead()
    {
        const int batchSize = 100;
        const int capacity = 2;

        var source = new InMemorySource<int>(Enumerable.Range(0, 100_000));
        var sink = new InMemorySink<int> { WriteDelay = TimeSpan.FromMilliseconds(20) };

        var pipeline = EtlPipeline.CreateBuilder("backpressure")
            .WithOptions(o =>
            {
                o.BatchSize = batchSize;
                o.ChannelCapacity = capacity;
            })
            .From(source)
            .To(sink)
            .Build();

        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(cts.Token);

        // Long enough for an unbounded source to race far ahead of a 20ms-per-batch sink: the sink
        // can have absorbed at most ~15 batches in this window, out of 1000 available.
        await Task.Delay(300, CancellationToken.None);

        var produced = source.Produced;
        var written = sink.Rows.Count;

        written.Should().BeGreaterThan(0, "the sink must have made progress");

        // The source can only be ahead by what the channel holds plus the batch in hand at each end.
        // Unbounded, it would have read all 100,000 rows within this window.
        var maxInFlight = batchSize * (capacity + 3);
        (produced - written).Should().BeLessThanOrEqualTo(
            maxInFlight,
            "a bounded channel must stall the source once the slow sink stops draining it");
        produced.Should().BeLessThan(100_000, "an unthrottled source would have drained the input by now");

        await cts.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // Expected: the run is abandoned once the measurement is taken.
        }
    }

    [Fact]
    public async Task Extract_and_load_overlap_instead_of_running_in_sequence()
    {
        // The whole point of the channel wiring. A sequential executor would finish every read
        // before the first write, so the sink's first write would land after the source's last read.
        var firstWrite = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastRead = 0L;

        var source = new TimestampedSource(50, () => Volatile.Write(ref lastRead, Stopwatch.GetTimestamp()));
        var sink = new TimestampedSink(t => firstWrite.TrySetResult(t));

        var pipeline = EtlPipeline.CreateBuilder("overlap")
            .WithOptions(o =>
            {
                o.BatchSize = 1;
                o.ChannelCapacity = 1;
            })
            .From<int>(source)
            .To(sink)
            .Build();

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        var firstWriteAt = await firstWrite.Task;
        firstWriteAt.Should().BeLessThan(
            Volatile.Read(ref lastRead),
            "the sink must start writing before the source finishes reading, or the stages are serialized");
    }

    [Fact]
    public async Task Recycles_batch_buffers_instead_of_churning_them()
    {
        // Struct rows mean the only per-batch allocation would be the batch array itself. If those
        // were not pooled, five million rows at a thousand per batch would be five thousand
        // large-object allocations; pooling keeps collections essentially flat.
        const int rows = 5_000_000;

        var pipeline = EtlPipeline.CreateBuilder("pooling")
            .WithOptions(o => o.BatchSize = 1_000)
            .From(new GeneratedSource(rows))
            .Select(m => new Measurement(m.Id, m.Value * 2))
            .To(new CountingSink())
            .Build();

        // Settle before measuring so start-up allocations are not attributed to the run.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var gen0Before = GC.CollectionCount(0);
        var result = await pipeline.RunAsync(CancellationToken.None);
        var gen0After = GC.CollectionCount(0);

        result.IsError.Should().BeFalse();
        result.Value.RowsWritten.Should().Be(rows);

        // Measured at zero: 5000 batch arrays are rented and returned without a single collection.
        // The bound is loose only to stay stable across machines, not because the margin is thin.
        (gen0After - gen0Before).Should().BeLessThan(
            10,
            "batch arrays come from the pool, so a five-million-row run should not churn the heap");
    }

    [Fact]
    public async Task Cancellation_stops_the_run_promptly_and_disposes_the_ports()
    {
        var source = new InMemorySource<int>(Enumerable.Range(0, 10_000_000))
        {
            ReadDelay = TimeSpan.FromMilliseconds(5),
        };
        var sink = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder("cancel")
            .WithOptions(o => o.BatchSize = 10)
            .From(source)
            .To(sink)
            .Build();

        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(cts.Token);

        await Task.Delay(50, CancellationToken.None);
        await cts.CancelAsync();

        var stopwatch = Stopwatch.StartNew();
        var result = await run;
        stopwatch.Stop();

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith("cancelled");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "cancellation must not wait for the whole source");

        source.Disposals.Should().Be(1);
        sink.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task Commits_the_sink_exactly_once_on_success()
    {
        var sink = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder("commit")
            .WithOptions(o => o.BatchSize = 4)
            .From(new InMemorySource<int>(Enumerable.Range(0, 100)))
            .To(sink)
            .Build();

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse();
        sink.Completions.Should().Be(1, "completion is a commit signal, not a per-batch flush");
        sink.Disposals.Should().Be(1);
    }

    [Fact]
    public async Task Does_not_commit_the_sink_when_the_run_fails()
    {
        // The reason completion is separate from disposal: disposal runs on the failure path too,
        // where committing a half-written load is exactly the wrong thing to do.
        var sink = new InMemorySink<int> { Reject = x => x == 42 };

        var pipeline = EtlPipeline.CreateBuilder("commit")
            .WithOptions(o => o.BatchSize = 4)
            .From(new InMemorySource<int>(Enumerable.Range(0, 100)))
            .To(sink)
            .Build();

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeTrue();
        sink.Completions.Should().Be(0);
        sink.Disposals.Should().Be(1, "disposal still has to happen so resources are released");
    }

    [Fact]
    public async Task Runs_coarse_job_stages_in_the_order_they_were_added()
    {
        var order = new List<string>();

        var pipeline = EtlPipeline.CreateBuilder("job")
            .AddStage("download", (_, _) =>
            {
                order.Add("download");
                return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
            })
            .AddStage("swap", (_, _) =>
            {
                order.Add("swap");
                return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
            })
            .Build();

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse();
        order.Should().Equal("download", "swap");
        result.Value.Stages.Select(s => s.Name).Should().Equal("download", "swap");
    }

    private sealed class TimestampedSource(int rows, Action onRead) : IDataSource<int>
    {
        private int _position;

        public async ValueTask<ErrorOr<int>> ReadAsync(Memory<int> buffer, CancellationToken cancellationToken)
        {
            if (_position >= rows)
            {
                return 0;
            }

            await Task.Delay(2, cancellationToken);
            buffer.Span[0] = _position++;
            onRead();
            return 1;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimestampedSink(Action<long> onWrite) : IDataSink<int>
    {
        public ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<int> batch, CancellationToken cancellationToken)
        {
            onWrite(Stopwatch.GetTimestamp());
            return ValueTask.FromResult<ErrorOr<int>>(batch.Length);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GeneratedSource(int total) : IDataSource<Measurement>
    {
        private int _produced;

        public ValueTask<ErrorOr<int>> ReadAsync(Memory<Measurement> buffer, CancellationToken cancellationToken)
        {
            var count = Math.Min(buffer.Length, total - _produced);
            if (count <= 0)
            {
                return ValueTask.FromResult<ErrorOr<int>>(0);
            }

            var span = buffer.Span;
            for (var i = 0; i < count; i++)
            {
                span[i] = new Measurement(_produced + i, (_produced + i) * 0.5);
            }

            _produced += count;
            return ValueTask.FromResult<ErrorOr<int>>(count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingSink : IDataSink<Measurement>
    {
        public ValueTask<ErrorOr<int>> WriteAsync(
            ReadOnlyMemory<Measurement> batch,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ErrorOr<int>>(batch.Length);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
