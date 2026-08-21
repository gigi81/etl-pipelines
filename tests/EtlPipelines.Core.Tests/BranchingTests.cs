using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Core.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Core.Tests;

/// <summary>
/// Fanning one stream out to several destinations.
/// </summary>
/// <remarks>
/// The failure modes here are quiet ones. A fan-out that drops a batch at a boundary still
/// "succeeds"; one that returns a pooled buffer twice corrupts unrelated data much later. So these
/// tests span many batches and check conservation rather than just that a run completed.
/// </remarks>
public class BranchingTests
{
    private const string PipelineName = "fanout";

    [Fact]
    public async Task Every_branch_receives_every_row()
    {
        //arrange
        // 2000 rows against a 16-row batch: ~125 batch boundaries for a fan-out to lose a batch at.
        var left = new InMemorySink<int>();
        var right = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o => o.BatchSize = 16)
            .From(new InMemorySource<int>(Enumerable.Range(0, 2_000)))
            .Branch(
                b1 => b1.To(left),
                b2 => b2.To(right))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        left.Rows.Should().Equal(Enumerable.Range(0, 2_000), "no row may be dropped or duplicated");
        right.Rows.Should().Equal(Enumerable.Range(0, 2_000));

        result.Value.RowsRead.Should().Be(2_000);
        result.Value.RowsWritten.Should().Be(4_000, "rows out is the total actually written");
    }

    [Fact]
    public async Task Branches_run_independent_transform_chains()
    {
        //arrange
        var raw = new InMemorySink<int>();
        var doubled = new InMemorySink<string>();

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o => o.BatchSize = 8)
            .From(new InMemorySource<int>(Enumerable.Range(1, 100)))
            .Branch(
                b1 => b1.Where(x => x % 2 == 0).To(raw),
                b2 => b2.Select(x => $"#{x * 2}").To(doubled))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // One branch filtering must not affect what the other sees.
        raw.Rows.Should().HaveCount(50);
        raw.Rows.Should().OnlyContain(x => x % 2 == 0);
        doubled.Rows.Should().HaveCount(100);
        doubled.Rows[0].Should().Be("#2");
    }

    [Fact]
    public async Task Recycles_per_branch_buffers_instead_of_leaking_them()
    {
        //arrange
        // Each branch is handed its own rented buffer. A missed Release in the fan-out pump is
        // invisible except as heap growth, so measure it: struct rows mean the batch arrays are the
        // only thing that could churn.
        const int rows = 2_000_000;

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o => o.BatchSize = 1_000)
            .From(new CountingSource(rows))
            .Branch(
                b1 => b1.To(new NullSink()),
                b2 => b2.Select(p => p).To(new NullSink()))
            .Build();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        //act
        var gen0Before = GC.CollectionCount(0);
        var result = await pipeline.RunAsync(CancellationToken.None);
        var gen0After = GC.CollectionCount(0);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsWritten.Should().Be(rows * 2);

        // Measured at zero: 2000 batches fanned to two branches rent and return 6000 arrays without a
        // single collection. The bound is loose only to stay stable across machines.
        (gen0After - gen0Before).Should().BeLessThan(
            50,
            "per-branch buffers must go back to the pool, not be abandoned");
    }

    [Fact]
    public async Task Does_not_hand_the_same_pooled_buffer_to_two_branches()
    {
        //arrange
        // Releasing one batch twice would return the same array to the pool twice, so a later rent
        // hands one array to two consumers and their contents tear into each other. Running many
        // batches through both branches and checking both are intact is what surfaces that.
        var left = new InMemorySink<int>();
        var right = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o =>
            {
                o.BatchSize = 4;
                o.ChannelCapacity = 1;
            })
            .From(new InMemorySource<int>(Enumerable.Range(0, 5_000)))
            .Branch(
                b1 => b1.To(left),
                b2 => b2.To(right))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();
        left.Rows.Should().Equal(Enumerable.Range(0, 5_000));
        right.Rows.Should().Equal(Enumerable.Range(0, 5_000), "a torn buffer shows up as wrong values here");
    }

    [Fact]
    public async Task A_slow_branch_throttles_the_whole_fan_out()
    {
        //arrange
        // Rows go to every branch before the next batch is taken, so the slowest branch governs.
        // Documented behaviour, and the alternative would be unbounded buffering for the fast one.
        var source = new InMemorySource<int>(Enumerable.Range(0, 100_000));
        var fast = new InMemorySink<int>();
        var slow = new InMemorySink<int> { WriteDelay = TimeSpan.FromMilliseconds(20) };

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o =>
            {
                o.BatchSize = 100;
                o.ChannelCapacity = 2;
            })
            .From(source)
            .Branch(
                b1 => b1.To(fast),
                b2 => b2.To(slow))
            .Build();

        using var cts = new CancellationTokenSource();

        //act
        var run = pipeline.RunAsync(cts.Token);
        await Task.Delay(300, CancellationToken.None);

        //assert
        source.Produced.Should().BeLessThan(100_000, "the slow branch must stall the source");

        // The real property. Comparing the fast branch against the source would prove nothing — it
        // can never receive more than was produced. What matters is that the fast branch cannot get
        // more than the in-flight capacity ahead of the slow one, which is what bounds memory: each
        // branch channel holds ChannelCapacity batches, plus one in hand at each end.
        const int maxLead = 100 * ((2 * 2) + 4);
        (fast.Rows.Count - slow.Rows.Count).Should().BeLessThanOrEqualTo(
            maxLead,
            "rows reach every branch before the next batch is taken, so the slowest one governs");

        await cts.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // Expected once the measurement is taken.
        }
    }

    [Fact]
    public async Task A_failing_branch_stops_the_whole_stage()
    {
        //arrange
        var healthy = new InMemorySink<int>();
        var failing = new InMemorySink<int> { Reject = x => x == 500 };

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o => o.BatchSize = 16)
            .From(new InMemorySource<int>(Enumerable.Range(0, 5_000)))
            .Branch(
                b1 => b1.To(healthy),
                b2 => b2.To(failing))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        // The important part is that this returns at all: the healthy branch must not be left
        // blocked on a channel nobody drains once its sibling gave up.
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("sink.rejected");
        healthy.Completions.Should().Be(0, "a failed stage commits nothing");
    }

    [Fact]
    public async Task Branches_can_themselves_branch()
    {
        //arrange
        var a = new InMemorySink<int>();
        var b = new InMemorySink<int>();
        var c = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o => o.BatchSize = 8)
            .From(new InMemorySource<int>(Enumerable.Range(0, 200)))
            .Branch(
                b1 => b1.To(a),
                b2 => b2.Branch(
                    b3 => b3.To(b),
                    b4 => b4.To(c)))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        a.Rows.Should().HaveCount(200);
        b.Rows.Should().HaveCount(200);
        c.Rows.Should().HaveCount(200);

        // A nested branch must be owned by its branch point, not leak out as an extra stage.
        result.Value.Stages.Should().ContainSingle();
        result.Value.RowsWritten.Should().Be(600);
    }

    [Fact]
    public void Rejects_a_branch_that_never_terminates()
    {
        //arrange
        var source = new InMemorySource<int>([1]);
        var sink = new InMemorySink<int>();

        //act
        var act = () => EtlPipeline.CreateBuilder(PipelineName)
            .From(source)
            .Branch(
                b1 => b1.To(sink),
                b2 => b2.Where(x => x > 0))
            .Build();

        //assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Branch 1 never terminated*");
    }

    [Fact]
    public void Rejects_a_branch_with_fewer_than_two_paths()
    {
        //arrange
        var source = new InMemorySource<int>([1]);
        var sink = new InMemorySink<int>();

        //act
        var act = () => EtlPipeline.CreateBuilder(PipelineName)
            .From(source)
            .Branch(b1 => b1.To(sink))
            .Build();

        //assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least two branches*");
    }

    [Fact]
    public void Registers_components_inside_branches_as_scoped_and_keyed()
    {
        //arrange
        var services = new ServiceCollection();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<CountingSource, int>()
            .Branch(
                b1 => b1.To<NullSink>(),
                b2 => b2.To<NullSink>()));

        //act
        var sinks = services.Where(d => d.ServiceType == typeof(IDataSink<int>)).ToArray();

        //assert
        // Two branches using the same sink type must not overwrite each other, which is exactly what
        // distinct keys buy — with a single unkeyed registration one branch would win for both.
        sinks.Should().HaveCount(2);
        sinks.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
        sinks.Select(d => d.ServiceKey!.ToString()).Should().OnlyHaveUniqueItems();
    }

    private sealed class CountingSource(int total) : IDataSource<int>
    {
        private int _produced;

        public CountingSource()
            : this(10)
        {
        }

        public ValueTask<ErrorOr<int>> ReadAsync(Memory<int> buffer, CancellationToken cancellationToken)
        {
            var count = Math.Min(buffer.Length, total - _produced);
            if (count <= 0)
            {
                return ValueTask.FromResult<ErrorOr<int>>(0);
            }

            var span = buffer.Span;
            for (var i = 0; i < count; i++)
            {
                span[i] = _produced + i;
            }

            _produced += count;
            return ValueTask.FromResult<ErrorOr<int>>(count);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullSink : IDataSink<int>
    {
        public ValueTask<ErrorOr<int>> WriteAsync(
            ReadOnlyMemory<int> batch,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ErrorOr<int>>(batch.Length);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
