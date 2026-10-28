using EtlPipelines.Management.V1;
using EtlPipelines.Server.Runs;

namespace EtlPipelines.Server.Tests.Runs;

/// <summary>
/// Fast tests for <see cref="RunStatusStore"/>'s eviction: a completed run's history is dropped
/// once it has been completed for longer than the retention period, and nothing else is.
/// </summary>
[Category("Server")]
public class RunStatusStoreTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    [Test]
    public async Task A_completed_run_is_evicted_once_another_run_completes_after_the_retention_period()
    {
        //arrange
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new RunStatusStore(clock, Retention);
        var oldRun = Guid.NewGuid();
        store.PublishRunCompleted(oldRun, new RunCompleted { Status = RunCompleted.Types.Status.Succeeded });

        clock.Now += Retention + TimeSpan.FromSeconds(1);

        //act
        store.PublishRunCompleted(Guid.NewGuid(), new RunCompleted { Status = RunCompleted.Types.Status.Succeeded });

        //assert
        store.IsTracked(oldRun).Should().BeFalse();
    }

    [Test]
    public async Task A_completed_run_within_the_retention_period_is_kept()
    {
        //arrange
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new RunStatusStore(clock, Retention);
        var recentRun = Guid.NewGuid();
        store.PublishRunCompleted(recentRun, new RunCompleted { Status = RunCompleted.Types.Status.Succeeded });

        clock.Now += Retention - TimeSpan.FromSeconds(1);

        //act
        store.PublishRunCompleted(Guid.NewGuid(), new RunCompleted { Status = RunCompleted.Types.Status.Succeeded });

        //assert
        store.IsTracked(recentRun).Should().BeTrue();
    }

    [Test]
    public async Task A_run_still_in_flight_is_never_evicted()
    {
        //arrange
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new RunStatusStore(clock, Retention);
        var inFlightRun = Guid.NewGuid();
        store.PublishStageCompleted(inFlightRun, new StageCompleted { Sequence = 0, Name = "seed" });

        clock.Now += Retention * 10;

        //act
        store.PublishRunCompleted(Guid.NewGuid(), new RunCompleted { Status = RunCompleted.Types.Status.Succeeded });

        //assert
        store.IsTracked(inFlightRun).Should().BeTrue();
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
