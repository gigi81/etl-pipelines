using EtlPipelines.Server.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Server.Tests.Live;

/// <summary><see cref="LiveSubscription"/>: remembers what changed, not every change, and never blocks a publisher.</summary>
[Category("Server")]
public class LiveSubscriptionTests
{
    private static LiveState Create() => new(TimeProvider.System, NullLogger<LiveState>.Instance);

    [Test]
    public async Task It_waits_for_a_change_and_wakes_as_soon_as_one_arrives()
    {
        //arrange
        var live = Create();
        using var subscription = live.Subscribe();
        var waiting = subscription.NextAsync(TimeSpan.FromSeconds(30), CancellationToken.None).AsTask();
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("nothing has changed yet");

        //act
        live.PipelinesChanged();

        //assert
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().Equal(new LiveChange(LiveChangeKind.Pipelines, Guid.Empty));
    }

    [Test]
    public async Task It_gives_up_after_the_timeout_with_nothing_so_a_keep_alive_can_be_sent()
    {
        using var subscription = Create().Subscribe();

        var changes = await subscription.NextAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        changes.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_straight_away_when_cancelled()
    {
        using var subscription = Create().Subscribe();
        using var cancel = new CancellationTokenSource();
        var waiting = subscription.NextAsync(TimeSpan.FromSeconds(30), cancel.Token).AsTask();

        await cancel.CancelAsync();

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEmpty();
    }

    [Test]
    public async Task Repeats_of_the_same_change_are_delivered_once()
    {
        var live = Create();
        using var subscription = live.Subscribe();

        for (var i = 0; i < 100; i++)
        {
            live.PipelinesChanged();
        }

        (await subscription.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().HaveCount(1);
        (await subscription.NextAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None)).Should().BeEmpty();
    }

    [Test]
    public async Task A_disposed_subscription_is_removed_and_stops_receiving()
    {
        var live = Create();
        var subscription = live.Subscribe();
        live.SubscriberCount.Should().Be(1);

        subscription.Dispose();
        subscription.Dispose();
        live.PipelinesChanged();

        live.SubscriberCount.Should().Be(0);
        (await subscription.NextAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None)).Should().BeEmpty();
    }
}
