using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Server.Tests.Live;

/// <summary><see cref="LiveState"/>: the server's status held in memory, backed by the database it is rebuilt from.</summary>
[Category("Server")]
public class LiveStateTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(100);

    [Test]
    public async Task It_loads_the_agents_and_the_runs_in_flight_or_recently_finished_on_first_use()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var agentId = await SeedAgentAsync(context);
        var inFlight = await SeedRunAsync(context, pipelineId, agentId, "Running", requested: now.UtcDateTime.AddDays(-3));
        var recent = await SeedRunAsync(context, pipelineId, agentId, "Succeeded", requested: now.UtcDateTime.AddMinutes(-40), completed: now.UtcDateTime.AddMinutes(-30));
        var old = await SeedRunAsync(context, pipelineId, agentId, "Succeeded", requested: now.UtcDateTime.AddHours(-4), completed: now.UtcDateTime.AddHours(-3));
        var live = Create(new ManualTimeProvider(now));

        //act
        var active = await live.ActiveRunsAsync(context, null, CancellationToken.None);

        //assert
        active.Select(run => run.Id).Should().Equal(inFlight);
        live.TryGetRun(recent).Should().NotBeNull("it finished within the retention window");
        live.TryGetRun(old).Should().BeNull("it finished before it");
        (await live.GetRunAsync(context, old, CancellationToken.None)).Should().NotBeNull("an old run is read from the database when asked for");
        (await live.ListAgentsAsync(context, CancellationToken.None)).Should().ContainSingle(agent => agent.Id == agentId);
    }

    [Test]
    public async Task Reads_come_from_memory_so_a_change_made_behind_its_back_is_not_seen()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var agentId = await SeedAgentAsync(context);
        var runId = await SeedRunAsync(context, pipelineId, agentId, "Running");
        var live = Create();
        await live.EnsureLoadedAsync(context, CancellationToken.None);

        //act
        context.StageResults.Add(new StageResult { Id = Guid.NewGuid(), RunId = runId, Sequence = 0, Name = "read", ElapsedMs = 1 });
        (await context.Agents.SingleAsync(a => a.Id == agentId)).Status = "Offline";
        await context.SaveChangesAsync();

        //assert
        (await live.GetRunAsync(context, runId, CancellationToken.None))!.Stages.Should().BeEmpty();
        (await live.ListAgentsAsync(context, CancellationToken.None)).Single().Status.Should().Be("Online");
    }

    [Test]
    public async Task A_stage_replaces_the_one_held_for_the_same_sequence_and_samples_keep_only_the_newest()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var runId = await SeedRunAsync(context, pipelineId, null, "Running");
        var live = Create();
        var start = DateTime.UtcNow;

        //act
        await live.StageCompletedAsync(context, runId, new StageDto(1, "transform", 5, 5, 0, 10, null, null), CancellationToken.None);
        await live.StageCompletedAsync(context, runId, new StageDto(0, "read", 0, 5, 0, 10, null, null), CancellationToken.None);
        await live.StageCompletedAsync(context, runId, new StageDto(0, "read", 0, 6, 0, 11, null, null), CancellationToken.None);
        for (var i = 0; i < MaxSamplesHeld + 10; i++)
        {
            await live.ResourceSampledAsync(context, runId, new ResourceSampleDto(start.AddSeconds(i), i, i), CancellationToken.None);
        }

        await live.ResourceSampledAsync(context, runId, new ResourceSampleDto(start.AddSeconds(5), 0, 0), CancellationToken.None);

        //assert
        var detail = live.TryGetRun(runId)!;
        detail.Stages.Select(s => (s.Sequence, s.RowsOut)).Should().Equal((0, 6), (1, 5));
        detail.Run.StagesCompleted.Should().Be(2);
        detail.ResourceSamples.Should().HaveCount(MaxSamplesHeld);
        detail.ResourceSamples[0].CpuPercent.Should().Be(10, "the ten oldest were dropped, and the repeat of an old timestamp ignored");
        detail.ResourceSamples.Select(s => s.SampledAtUtc).Should().BeInAscendingOrder();
    }

    [Test]
    public async Task A_burst_of_changes_to_one_run_is_one_notice_for_the_run_and_one_for_its_agent()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var agentId = await SeedAgentAsync(context);
        var runId = await SeedRunAsync(context, pipelineId, agentId, "Running");
        var other = await SeedRunAsync(context, pipelineId, null, "Running");
        var live = Create();
        await live.EnsureLoadedAsync(context, CancellationToken.None);

        using var everything = live.Subscribe();
        using var onlyThisRun = live.Subscribe(change => change is { Kind: LiveChangeKind.Run } && change.Id == runId);
        using var onlyTheOtherRun = live.Subscribe(change => change is { Kind: LiveChangeKind.Run } && change.Id == other);

        //act
        for (var i = 0; i < 5; i++)
        {
            await live.StageCompletedAsync(context, runId, new StageDto(i, $"s{i}", 0, 0, 0, 0, null, null), CancellationToken.None);
        }

        //assert
        (await everything.NextAsync(Brief, CancellationToken.None)).Should().BeEquivalentTo(
            [new LiveChange(LiveChangeKind.Run, runId), new LiveChange(LiveChangeKind.Agent, agentId)]);
        (await onlyThisRun.NextAsync(Brief, CancellationToken.None)).Should().Equal(new LiveChange(LiveChangeKind.Run, runId));
        (await onlyTheOtherRun.NextAsync(Brief, CancellationToken.None)).Should().BeEmpty();
        (await everything.NextAsync(Brief, CancellationToken.None)).Should().BeEmpty("nothing is repeated once it has been read");
    }

    [Test]
    public async Task A_run_changing_status_is_reflected_for_its_agent_and_old_finished_runs_are_evicted()
    {
        //arrange
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var agentId = await SeedAgentAsync(context);
        var first = await SeedRunAsync(context, pipelineId, agentId, "Running");
        var second = await SeedRunAsync(context, pipelineId, agentId, "Running");
        var live = Create(clock);
        (await live.ListAgentsAsync(context, CancellationToken.None)).Single().ActiveRuns.Should().HaveCount(2);

        // The first run finishes...
        await FinishAsync(context, live, first, clock);
        (await live.ListAgentsAsync(context, CancellationToken.None)).Single().ActiveRuns.Select(r => r.Id).Should().Equal(second);

        //act - ...and by the time the second one does, it is long past retention
        clock.Advance(LiveState.Retention + TimeSpan.FromMinutes(1));
        await FinishAsync(context, live, second, clock);

        //assert
        live.TryGetRun(first).Should().BeNull("an hour after it finished");
        live.TryGetRun(second).Should().NotBeNull();
        (await live.GetRunAsync(context, first, CancellationToken.None)).Should().NotBeNull("it is still in the database");
    }

    [Test]
    public async Task A_failing_update_never_throws_and_the_state_is_rebuilt_from_the_database_next_time()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        await SeedAgentAsync(context);
        var live = Create();
        await live.EnsureLoadedAsync(context, CancellationToken.None);

        var broken = SqliteServerDbContext.Create();
        await broken.DisposeAsync();

        //act - an update for a run the state does not hold has to read it, and the context is dead
        var update = () => live.StageCompletedAsync(broken, Guid.NewGuid(), new StageDto(0, "x", 0, 0, 0, 0, null, null), CancellationToken.None);
        await update.Should().NotThrowAsync();

        await SeedAgentAsync(context, "late");

        //assert
        (await live.ListAgentsAsync(context, CancellationToken.None)).Select(a => a.MachineName).Should().Contain("late");
    }

    [Test]
    public async Task An_agent_registering_heartbeating_and_going_offline_is_tracked_with_its_uptime()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var context = SqliteServerDbContext.Create();
        var live = Create(new ManualTimeProvider(now));
        var agent = new Database.Entities.Agent
        {
            Id = Guid.NewGuid(), MachineName = "a", Tags = [], Version = "1", Status = "Online", LastHeartbeatAt = now.UtcDateTime,
            Info = $$"""{"startedAtUtc":"{{now.AddHours(-1):O}}"}""",
        };
        context.Agents.Add(agent);
        await context.SaveChangesAsync();

        //act + assert
        await live.AgentRegisteredAsync(context, agent, CancellationToken.None);
        live.TryGetAgent(agent.Id)!.UptimeSeconds.Should().BeInRange(3599, 3601);

        await live.AgentsOfflineAsync(context, [agent.Id], CancellationToken.None);
        live.TryGetAgent(agent.Id)!.Status.Should().Be("Offline");
        live.TryGetAgent(agent.Id)!.UptimeSeconds.Should().BeNull();

        await live.AgentHeartbeatAsync(context, agent.Id, now.UtcDateTime.AddSeconds(10), CancellationToken.None);
        live.TryGetAgent(agent.Id)!.Status.Should().Be("Online");
        live.TryGetAgent(agent.Id)!.LastHeartbeatAtUtc.Should().Be(now.UtcDateTime.AddSeconds(10));
    }

    // What LiveRun keeps per run (internal to the Server, so repeated here).
    private const int MaxSamplesHeld = 120;

    private static LiveState Create(TimeProvider? clock = null) => new(clock ?? TimeProvider.System, NullLogger<LiveState>.Instance);

    private static async Task FinishAsync(ServerDbContext context, LiveState live, Guid runId, TimeProvider clock)
    {
        var run = await context.Runs.SingleAsync(r => r.Id == runId);
        run.Status = "Succeeded";
        run.CompletedAt = clock.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync();
        await live.RunChangedAsync(context, run, CancellationToken.None);
    }

    private static async Task<Guid> SeedPipelineAsync(ServerDbContext context)
    {
        var package = new Package { Id = Guid.NewGuid(), NugetPackageId = "Pkg", CreatedAt = DateTime.UtcNow };
        var version = new PackageVersion { Id = Guid.NewGuid(), PackageId = package.Id, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" };
        var pipeline = new Pipeline { Id = Guid.NewGuid(), PackageVersionId = version.Id, Name = "p", CreatedAt = DateTime.UtcNow };
        context.AddRange(package, version, pipeline);
        await context.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<Guid> SeedAgentAsync(ServerDbContext context, string name = "agent-01")
    {
        var agent = new Database.Entities.Agent
        {
            Id = Guid.NewGuid(), MachineName = name, Tags = [], Version = "1.0.0", Status = "Online", LastHeartbeatAt = DateTime.UtcNow,
        };
        context.Agents.Add(agent);
        await context.SaveChangesAsync();
        return agent.Id;
    }

    private static async Task<Guid> SeedRunAsync(
        ServerDbContext context, Guid pipelineId, Guid? agentId, string status, DateTime? requested = null, DateTime? completed = null)
    {
        var run = new Run
        {
            Id = Guid.NewGuid(), PipelineId = pipelineId, AgentId = agentId, Status = status,
            RequestedAt = requested ?? DateTime.UtcNow, CompletedAt = completed,
        };
        context.Runs.Add(run);
        await context.SaveChangesAsync();
        return run.Id;
    }
}
