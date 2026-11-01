using EtlPipelines.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Agent.Tests;

/// <summary><see cref="WorkScheduler"/>: work items running side by side, within the limit.</summary>
[Category("Agent")]
public class WorkSchedulerTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);

    [Test]
    public async Task Limited_work_runs_up_to_the_limit_and_the_rest_waits_for_a_slot()
    {
        //arrange
        var scheduler = new WorkScheduler(2, NullLogger.Instance);
        var started = new List<int>();
        var gates = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource()).ToArray();

        //act
        for (var i = 0; i < 4; i++)
        {
            var number = i;
            _ = scheduler.RunLimited(
                async _ =>
                {
                    lock (started)
                    {
                        started.Add(number);
                    }

                    await gates[number].Task;
                },
                CancellationToken.None);
        }

        await Task.Delay(Brief);

        //assert - two are going, two wait (which two is up to the thread pool)
        started.Should().HaveCount(2);
        (scheduler.Running, scheduler.Waiting).Should().Be((2, 2));

        // a slot frees up: the next one in goes
        gates[started[0]].SetResult();
        await Eventually(() => started.Count == 3);
        (scheduler.Running, scheduler.Waiting).Should().Be((2, 1));

        foreach (var gate in gates)
        {
            gate.TrySetResult();
        }

        await scheduler.WhenIdleAsync();
        started.Should().HaveCount(4);
        (scheduler.Running, scheduler.Waiting).Should().Be((0, 0));
    }

    [Test]
    public async Task Unlimited_work_starts_at_once_even_when_every_run_slot_is_taken()
    {
        //arrange - one slot, taken
        var scheduler = new WorkScheduler(1, NullLogger.Instance);
        var slotHeld = new TaskCompletionSource();
        _ = scheduler.RunLimited(_ => slotHeld.Task, CancellationToken.None);
        await Eventually(() => scheduler.Running == 1);
        var installed = false;

        //act
        _ = scheduler.RunUnlimited(
            _ =>
            {
                installed = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        //assert
        await Eventually(() => installed);
        scheduler.Running.Should().Be(1, "the run is still going");

        slotHeld.SetResult();
        await scheduler.WhenIdleAsync();
    }

    [Test]
    public async Task Work_that_fails_does_not_escape_and_gives_its_slot_back()
    {
        //arrange
        var scheduler = new WorkScheduler(1, NullLogger.Instance);
        var secondRan = false;

        //act
        _ = scheduler.RunLimited(_ => throw new InvalidOperationException("boom"), CancellationToken.None);
        _ = scheduler.RunLimited(
            _ =>
            {
                secondRan = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        await scheduler.WhenIdleAsync();

        //assert
        secondRan.Should().BeTrue();
        scheduler.Running.Should().Be(0);
    }

    [Test]
    public async Task Work_still_waiting_when_the_agent_stops_never_starts_and_idle_is_reached()
    {
        //arrange
        var scheduler = new WorkScheduler(1, NullLogger.Instance);
        using var stopping = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var started = 0;

        // two items for one slot, both of which would stay until cancelled: whichever gets the slot starts, the other waits
        for (var i = 0; i < 2; i++)
        {
            _ = scheduler.RunLimited(
                async token =>
                {
                    Interlocked.Increment(ref started);
                    await never.Task.WaitAsync(token);
                },
                stopping.Token);
        }

        await Eventually(() => scheduler.Running == 1 && scheduler.Waiting == 1);

        //act
        await stopping.CancelAsync();
        await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        //assert
        started.Should().Be(1, "the one that was waiting never started");
        (scheduler.Running, scheduler.Waiting).Should().Be((0, 0));
    }

    internal static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(10);
        }
    }
}
