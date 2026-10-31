using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Live.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Server.Tests.Live;

/// <summary>
/// <see cref="LiveStateSync"/>: loads the state at startup and writes what happens to it back to the
/// database - behind, in batches, and without losing or reordering anything when the database is
/// unwell.
/// </summary>
[Category("Server")]
public class LiveStateSyncTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    [Test]
    public async Task A_flush_writes_everything_waiting_in_one_batch_and_a_second_one_has_nothing_to_do()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync();
        var (live, sync) = Create(database.ScopeFactory);
        await sync.StartAsync(CancellationToken.None);
        var agentId = await live.RegisterAgentAsync("a", [], "1", null, CancellationToken.None);
        var runId = await live.CreateRunAsync(pipelineId, "p", "Pkg", "1", agentId, [], CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "read", 0, 1, 0, 1, null, null), CancellationToken.None);

        (await database.ReadAsync(context => context.Runs.CountAsync())).Should().Be(0, "nothing is written until a sync");

        //act
        var written = await sync.FlushAsync(CancellationToken.None);
        var again = await sync.FlushAsync(CancellationToken.None);

        //assert
        written.Should().Be(3);
        again.Should().Be(0);
        live.PendingEventCount.Should().Be(0);
        (await database.ReadAsync(context => context.Runs.CountAsync())).Should().Be(1);
        (await database.ReadAsync(context => context.StageResults.CountAsync())).Should().Be(1);
        await sync.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Starting_loads_the_state_from_the_database_and_lets_waiting_callers_through()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        await database.ReadAsync(async context =>
        {
            context.Agents.Add(new Database.Entities.Agent { Id = Guid.NewGuid(), MachineName = "from-db", Tags = [], Version = "1", Status = "Online", LastHeartbeatAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
            return 0;
        });
        var (live, sync) = Create(database.ScopeFactory);
        var waiting = live.ListAgentsAsync(CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("the state is not loaded yet");

        //act
        await sync.StartAsync(CancellationToken.None);

        //assert
        (await waiting.WaitAsync(Soon)).Select(a => a.MachineName).Should().Equal("from-db");
        await sync.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task On_its_own_timer_it_writes_what_happened_and_leaves_the_database_alone_when_nothing_did()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var counted = new FlakyScopeFactory(database.ScopeFactory, failures: 0);
        var (live, sync) = Create(counted, interval: TimeSpan.FromMilliseconds(50));
        await sync.StartAsync(CancellationToken.None);
        var startupScopes = counted.Attempts;

        //act + assert - idle: no scope, so no database work, however many ticks pass
        await Task.Delay(300);
        counted.Attempts.Should().Be(startupScopes, "with nothing to write the database is not touched");

        await live.RegisterAgentAsync("a", [], "1", null, CancellationToken.None);
        await WaitUntilAsync(async () => await database.ReadAsync(context => context.Agents.CountAsync()) == 1);
        await sync.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task A_batch_that_fails_is_tried_again_with_what_happened_meanwhile_kept_in_order()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync();
        var flaky = new FlakyScopeFactory(database.ScopeFactory, failures: 0);
        var (live, sync) = Create(flaky);
        await sync.StartAsync(CancellationToken.None);

        var runId = await live.CreateRunAsync(pipelineId, "p", "Pkg", "1", null, [], CancellationToken.None);
        var failing = new FlakyScopeFactory(database.ScopeFactory, failures: 1);
        var (live2, sync2) = Create(failing, live);

        //act - the first write fails; the run starts while it is waiting to be retried
        (await sync2.FlushAsync(CancellationToken.None)).Should().Be(0);
        live2.PendingEventCount.Should().Be(1, "the batch went back");
        await live2.StartRunAsync(runId, CancellationToken.None);
        live2.PendingEventCount.Should().Be(2);
        (await sync2.FlushAsync(CancellationToken.None)).Should().Be(2);

        //assert - written in the order it happened: had the start gone first it would have found no run
        var run = await database.ReadAsync(context => context.Runs.SingleAsync());
        run.Status.Should().Be("Running");
        run.StartedAt.Should().NotBeNull();
    }

    [Test]
    public async Task An_event_the_database_keeps_refusing_is_isolated_and_dropped_so_it_cannot_block_the_rest()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync();
        var existing = Guid.NewGuid();
        await database.ReadAsync(async context =>
        {
            context.Runs.Add(new Run { Id = existing, PipelineId = pipelineId, Status = "Succeeded", RequestedAt = DateTime.UtcNow });
            await context.SaveChangesAsync();
            return 0;
        });

        var (live, sync) = Create(database.ScopeFactory);
        await sync.StartAsync(CancellationToken.None);
        live.RequeuePendingEvents(
        [
            new AgentRegisteredEvent(Guid.NewGuid(), "good-1", [], "1", null, DateTime.UtcNow),
            new RunCreatedEvent(existing, pipelineId, "p", "Pkg", "1", null, [], DateTime.UtcNow), // a run that already exists: the database refuses it
            new AgentRegisteredEvent(Guid.NewGuid(), "good-2", [], "1", null, DateTime.UtcNow),
        ]);

        //act - twice it is retried whole, the third time one by one
        (await sync.FlushAsync(CancellationToken.None)).Should().Be(0);
        live.PendingEventCount.Should().Be(3);
        (await sync.FlushAsync(CancellationToken.None)).Should().Be(0);
        var written = await sync.FlushAsync(CancellationToken.None);

        //assert
        written.Should().Be(2, "everything but the poison event");
        live.PendingEventCount.Should().Be(0, "the poison event is dropped, not retried forever");
        (await database.ReadAsync(context => context.Agents.Select(a => a.MachineName).OrderBy(n => n).ToListAsync())).Should().Equal("good-1", "good-2");
    }

    [Test]
    public async Task Stopping_writes_whatever_is_still_waiting_so_a_clean_shutdown_loses_nothing()
    {
        //arrange - a long interval, so the timer never fires
        await using var database = await TestDatabase.CreateAsync();
        var (live, sync) = Create(database.ScopeFactory, interval: TimeSpan.FromHours(1));
        await sync.StartAsync(CancellationToken.None);
        await live.RegisterAgentAsync("last-words", [], "1", null, CancellationToken.None);

        //act
        await sync.StopAsync(CancellationToken.None);

        //assert
        (await database.ReadAsync(context => context.Agents.Select(a => a.MachineName).ToListAsync())).Should().Equal("last-words");
    }

    [Test]
    public async Task After_a_restart_the_state_is_what_the_last_sync_wrote()
    {
        //arrange - a server runs, syncs, and then dies with a few events still unwritten
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync();
        var (live, sync) = Create(database.ScopeFactory);
        await sync.StartAsync(CancellationToken.None);
        var agentId = await live.RegisterAgentAsync("a", [], "1", null, CancellationToken.None);
        var runId = await live.CreateRunAsync(pipelineId, "p", "Pkg", "1", agentId, [], CancellationToken.None);
        await live.StartRunAsync(runId, CancellationToken.None);
        await sync.FlushAsync(CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "lost", 0, 1, 0, 1, null, null), CancellationToken.None);
        live.PendingEventCount.Should().Be(1);

        //act - a new process
        var (restarted, restartedSync) = Create(database.ScopeFactory);
        await restartedSync.StartAsync(CancellationToken.None);

        //assert - the run is still there, running, with what had been written; what had not is gone
        var run = restarted.TryGetRun(runId)!;
        run.Run.Status.Should().Be("Running");
        run.Stages.Should().BeEmpty();
        (await restarted.ActiveRunsAsync(null, CancellationToken.None)).Should().ContainSingle(r => r.Id == runId);
        await restartedSync.StopAsync(CancellationToken.None);
    }

    private static (LiveState Live, LiveStateSync Sync) Create(IServiceScopeFactory scopeFactory, LiveState? live = null, TimeSpan? interval = null)
    {
        live ??= new LiveState(TimeProvider.System, NullLogger<LiveState>.Instance);
        var sync = new LiveStateSync(
            scopeFactory,
            live,
            Options.Create(new LiveStateOptions { SyncInterval = interval ?? TimeSpan.FromHours(1) }),
            TimeProvider.System,
            NullLogger<LiveStateSync>.Instance);

        return (live, sync);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Soon;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(25);
        }
    }
}
