using System.Diagnostics;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Core.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Core.Tests;

/// <summary>
/// Running several independent stage chains concurrently as one block, the coarse-stage counterpart
/// to <see cref="IDataflowBuilder{TRow}.Branch"/> — which fans one already-flowing stream out, rather
/// than running unrelated chains side by side.
/// </summary>
public class ParallelTests
{
    private const string PipelineName = "parallel";

    [Test]
    public async Task Branches_run_at_the_same_time_rather_than_one_after_another()
    {
        //arrange
        // Four branches at 150ms each: sequential is >= 600ms, concurrent stays close to 150ms.
        var delay = TimeSpan.FromMilliseconds(150);

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .Parallel(
                b => b.AddStage("branch-0", async (_, ct) => { await Task.Delay(delay, ct); return Result.Success; }),
                b => b.AddStage("branch-1", async (_, ct) => { await Task.Delay(delay, ct); return Result.Success; }),
                b => b.AddStage("branch-2", async (_, ct) => { await Task.Delay(delay, ct); return Result.Success; }),
                b => b.AddStage("branch-3", async (_, ct) => { await Task.Delay(delay, ct); return Result.Success; }))
            .Build();

        //act
        var started = Stopwatch.StartNew();
        var result = await pipeline.RunAsync(CancellationToken.None);
        var elapsed = started.Elapsed;

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        elapsed.Should().BeLessThan(delay * 3, "four 150ms branches run one after another would take 600ms+");
    }

    [Test]
    public async Task Sums_row_counts_across_branches_into_one_stage()
    {
        //arrange
        var left = new InMemorySink<int>();
        var right = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .Parallel(
                b => b.From(new InMemorySource<int>(Enumerable.Range(0, 10))).To(left),
                b => b.From(new InMemorySource<int>(Enumerable.Range(0, 25))).To(right))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be("Parallel(2)");
        result.Value.RowsRead.Should().Be(35, "10 from the first branch and 25 from the second");
        result.Value.RowsWritten.Should().Be(35);
        left.Rows.Should().HaveCount(10);
        right.Rows.Should().HaveCount(25);
    }

    [Test]
    public async Task A_branch_that_does_not_move_rows_does_not_zero_out_the_others()
    {
        //arrange
        // Mirrors the sample: a TruncateTable-shaped no-op stage ahead of a real dataflow per branch.
        var sink = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .Parallel(
                b => b
                    .AddStage("truncate", (_, _) => ValueTask.FromResult<ErrorOr<Success>>(Result.Success))
                    .From(new InMemorySource<int>(Enumerable.Range(0, 5)))
                    .To(sink),
                b => b.AddStage("side-effect-only", (_, _) => ValueTask.FromResult<ErrorOr<Success>>(Result.Success)))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(5, "the coarse stages moved nothing, so they must not be counted as rows");
        result.Value.RowsWritten.Should().Be(5);
    }

    [Test]
    public async Task A_failing_branch_does_not_stop_a_healthy_one_from_finishing()
    {
        //arrange
        // Unlike a dataflow Branch — which shares one execution and stops the whole fan-out — these
        // are independent chains. Cutting a healthy one off mid-write would trade a slow run for a
        // half-written destination, so it is left to finish.
        var healthy = new InMemorySink<int>();
        var failing = new InMemorySink<int> { Reject = x => x == 2 };

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .Parallel(
                b => b.From(new InMemorySource<int>(Enumerable.Range(0, 5))).To(healthy),
                b => b.From(new InMemorySource<int>([1, 2, 3])).To(failing))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("sink.rejected");
        healthy.Completions.Should().Be(1, "the healthy branch ran to completion rather than being cut off");
        healthy.Rows.Should().HaveCount(5);
    }

    [Test]
    public async Task A_failed_branch_still_counts_what_it_moved_before_failing()
    {
        //arrange
        var failing = new InMemorySink<int> { Reject = x => x == 999 };

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .Parallel(
                b => b.From(new InMemorySource<int>([1, 2, 3])).To(new InMemorySink<int>()),
                b => b.From(new InMemorySource<int>([999])).To(failing))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();

        // The failed branch's own rows-in still rides along on the error, exactly as a single failed
        // dataflow stage already reports what it consumed before it died.
        var failed = result.Errors.Should().ContainSingle().Subject;
        failed.Metadata.Should().NotBeNull();
    }

    [Test]
    public void Rejects_fewer_than_two_branches()
    {
        //act
        var act = () => EtlPipeline.CreateBuilder(PipelineName)
            .Parallel(b => b.AddStage("only", (_, _) => ValueTask.FromResult<ErrorOr<Success>>(Result.Success)))
            .Build();

        //assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least two branches*");
    }

    [Test]
    public void Registers_components_inside_branches_as_scoped_and_keyed()
    {
        //arrange
        var services = new ServiceCollection();
        services.AddEtlPipeline(PipelineName, builder => builder
            .Parallel(
                b => b.From<CountingSource, int>().To<NullSink>(),
                b => b.From<CountingSource, int>().To<NullSink>()));

        //act
        var sources = services.Where(d => d.ServiceType == typeof(IDataSource<int>)).ToArray();

        //assert
        // Two branches using the same source type must not overwrite each other's registration - the
        // same requirement a dataflow Branch already has to meet, now one level up.
        sources.Should().HaveCount(2);
        sources.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
        sources.Select(d => d.ServiceKey!.ToString()).Should().OnlyHaveUniqueItems();
    }

    private sealed class CountingSource : IDataSource<int>
    {
        private int _produced;

        public ValueTask<ErrorOr<int>> ReadAsync(Memory<int> buffer, CancellationToken cancellationToken)
        {
            var count = Math.Min(buffer.Length, 3 - _produced);
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
        public ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<int> batch, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ErrorOr<int>>(batch.Length);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
