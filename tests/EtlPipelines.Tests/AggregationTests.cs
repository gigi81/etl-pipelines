using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Tests.Fixtures;
using FluentAssertions;

namespace EtlPipelines.Tests;

/// <summary>
/// Cardinality-changing transforms: the cases where rows-in and rows-out legitimately differ.
/// </summary>
/// <remarks>
/// These exist because the obvious batch contract — one call, one produced count — quietly cannot
/// express them. Aggregation spans batch boundaries and emits only after input ends; expansion can
/// overflow the output buffer from a single input row. Both would appear to work on a single small
/// batch and fail on real data, so every test here deliberately spans many batches.
/// </remarks>
public class AggregationTests
{
    private const string PipelineName = "aggregate";

    private sealed record Sale(string Region, int Amount);

    private sealed record RegionTotal(string Region, int Total, int Count);

    /// <summary>Builds sales where each region's rows are contiguous, as an ORDER BY would produce.</summary>
    private static Sale[] SortedSales(int regions, int perRegion) =>
        [.. Enumerable.Range(0, regions).SelectMany(r =>
            Enumerable.Range(0, perRegion).Select(_ => new Sale($"r{r:D4}", r + 1)))];

    private static IPipeline BuildGroupBy(
        IDataSource<Sale> source,
        IDataSink<RegionTotal> sink,
        bool sorted,
        int batchSize) =>
        EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o => o.BatchSize = batchSize)
            .From(source)
            .GroupBy(
                s => s.Region,
                _ => (Total: 0, Count: 0),
                (state, s) => (state.Total + s.Amount, state.Count + 1),
                (region, state) => new RegionTotal(region, state.Total, state.Count),
                inputIsSortedByKey: sorted)
            .To(sink)
            .Build();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Groups_rows_that_straddle_batch_boundaries(bool sorted)
    {
        //arrange
        // 50 rows per region against a 64-row batch means almost every group is split across at
        // least one batch boundary. An implementation that aggregated only within a batch would
        // emit each region several times with partial totals, and still look "successful".
        const int regions = 200;
        const int perRegion = 50;

        var sink = new InMemorySink<RegionTotal>();
        var pipeline = BuildGroupBy(
            new InMemorySource<Sale>(SortedSales(regions, perRegion)),
            sink,
            sorted,
            batchSize: 64);

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        sink.Rows.Should().HaveCount(regions, "each region must appear exactly once, not once per batch it touched");
        sink.Rows.Select(r => r.Region).Should().OnlyHaveUniqueItems();
        sink.Rows.Should().OnlyContain(r => r.Count == perRegion);

        // Conservation: nothing invented, nothing dropped.
        var expectedTotal = Enumerable.Range(0, regions).Sum(r => (r + 1) * perRegion);
        sink.Rows.Sum(r => r.Total).Should().Be(expectedTotal);

        // Cardinality really changed, and the result says so rather than reporting them equal.
        result.Value.RowsRead.Should().Be(regions * perRegion);
        result.Value.RowsWritten.Should().Be(regions);
    }

    [Fact]
    public async Task Sorted_aggregate_emits_before_its_input_is_exhausted()
    {
        //arrange
        // The point of the sorted path: a key change proves the group is done, so results flow while
        // the source is still open, and memory stays at one accumulator instead of one per key.
        var source = new GatedSource<Sale>(SortedSales(regions: 100, perRegion: 10), gateAfter: 500);
        var sink = new InMemorySink<RegionTotal>();
        var pipeline = BuildGroupBy(source, sink, sorted: true, batchSize: 32);

        //act
        var run = pipeline.RunAsync(CancellationToken.None);
        await WaitUntil(() => sink.Rows.Count > 0, TimeSpan.FromSeconds(5));

        //assert
        sink.Rows.Should().NotBeEmpty("a sorted aggregate is only semi-blocking");
        source.Produced.Should().Be(500, "the source is still gated, so input is provably not exhausted");

        source.Release();
        var result = await run;

        result.IsError.Should().BeFalse();
        sink.Rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Hash_aggregate_emits_nothing_until_its_input_is_exhausted()
    {
        //arrange
        // The counterpart: without an ordering guarantee no group can be declared finished early, so
        // this path is fully blocking. Asserting it keeps the trade-off honest rather than implied.
        var source = new GatedSource<Sale>(SortedSales(regions: 100, perRegion: 10), gateAfter: 500);
        var sink = new InMemorySink<RegionTotal>();
        var pipeline = BuildGroupBy(source, sink, sorted: false, batchSize: 32);

        //act
        var run = pipeline.RunAsync(CancellationToken.None);
        await WaitUntil(() => source.Produced >= 500, TimeSpan.FromSeconds(5));
        await Task.Delay(100, CancellationToken.None);

        //assert
        sink.Rows.Should().BeEmpty("a hash aggregate cannot emit until every row has arrived");

        source.Release();
        var result = await run;

        result.IsError.Should().BeFalse();
        sink.Rows.Should().HaveCount(100);
    }

    [Fact]
    public async Task Drains_more_groups_than_fit_in_a_single_output_buffer()
    {
        //arrange
        // 5000 groups draining through a 16-row buffer: the drain must be called repeatedly until it
        // reports zero, which is why it mirrors the source contract rather than being a single flush.
        var sink = new InMemorySink<RegionTotal>();
        var pipeline = BuildGroupBy(
            new InMemorySource<Sale>(SortedSales(regions: 5_000, perRegion: 2)),
            sink,
            sorted: false,
            batchSize: 16);

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();
        sink.Rows.Should().HaveCount(5_000);
        sink.Rows.Select(r => r.Region).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Expands_one_row_into_more_rows_than_the_output_buffer_holds()
    {
        //arrange
        // Each input row expands to 100 outputs while the buffer holds 8, so the transform must stop
        // mid-row, report partial consumption, and resume exactly where it left off. Losing or
        // repeating values here is the failure mode a single-count contract cannot even detect.
        var sink = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder("expand")
            .WithOptions(o => o.BatchSize = 8)
            .From(new InMemorySource<int>(Enumerable.Range(0, 50)))
            .SelectMany(seed => Enumerable.Range(seed * 100, 100))
            .To(sink)
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        sink.Rows.Should().HaveCount(5_000);
        sink.Rows.Should().OnlyHaveUniqueItems("no value may be emitted twice when resuming mid-row");
        sink.Rows.Order().Should().Equal(Enumerable.Range(0, 5_000));
        result.Value.RowsRead.Should().Be(50);
        result.Value.RowsWritten.Should().Be(5_000);
    }

    [Fact]
    public void Refuses_to_parallelise_a_stateful_transform()
    {
        //arrange
        // Parallel workers would each accumulate and drain a separate partial aggregate. That is
        // wrong output rather than a crash, so the builder rejects it up front.
        var source = new InMemorySource<Sale>(SortedSales(4, 4));
        var sink = new InMemorySink<RegionTotal>();

        //act
        var act = () => EtlPipeline.CreateBuilder(PipelineName)
            .From(source)
            .GroupBy(
                s => s.Region,
                _ => 0,
                (total, s) => total + s.Amount,
                (region, total) => new RegionTotal(region, total, 0))
            .WithParallelism(4)
            .To(sink)
            .Build();

        //assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*carries state across batches*");
    }

    [Fact]
    public async Task Allows_parallelism_on_a_stateless_transform()
    {
        //arrange
        var sink = new InMemorySink<int>();

        var pipeline = EtlPipeline.CreateBuilder("parallel")
            .WithOptions(o => o.BatchSize = 32)
            .From(new InMemorySource<int>(Enumerable.Range(0, 1_000)))
            .Select(x => x * 2)
            .WithParallelism(4)
            .To(sink)
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // Order is not preserved at a parallelism above one, so compare as a set.
        sink.Rows.Order().Should().Equal(Enumerable.Range(0, 1_000).Select(x => x * 2));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, CancellationToken.None);
        }
    }
}
