using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Live.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Server.Tests.Live;

/// <summary>
/// <see cref="LiveState"/>: the server's one store, held in memory - every change an event applied to
/// the state and queued for the database in one step.
/// </summary>
[Category("Server")]
public class LiveStateTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(100);

    // What LiveRun keeps per run (internal to the Server, so repeated here).
    private const int MaxSamplesHeld = 120;

    [Test]
    public async Task It_loads_the_agents_and_the_runs_in_flight_or_recently_finished()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var agentId = await SeedAgentAsync(context);
        var inFlight = await SeedRunAsync(context, pipelineId, agentId, "Running", requested: now.UtcDateTime.AddDays(-3));
        var recent = await SeedRunAsync(context, pipelineId, agentId, "Succeeded", requested: now.UtcDateTime.AddMinutes(-40), completed: now.UtcDateTime.AddMinutes(-30));
        var old = await SeedRunAsync(context, pipelineId, agentId, "Succeeded", requested: now.UtcDateTime.AddHours(-4), completed: now.UtcDateTime.AddHours(-3));

        //act
        var live = await TestLiveState.CreateAsync(context, new ManualTimeProvider(now));

        //assert
        (await live.ActiveRunsAsync(null, CancellationToken.None)).Select(run => run.Id).Should().Equal(inFlight);
        live.TryGetRun(recent).Should().NotBeNull("it finished within the retention window");
        live.TryGetRun(old).Should().BeNull("it finished before it");
        (await live.GetRunAsync(context, old, CancellationToken.None)).Should().NotBeNull("an old run is read from the database when asked for");
        (await live.ListAgentsAsync(CancellationToken.None)).Should().ContainSingle(agent => agent.Id == agentId);
        live.PendingEventCount.Should().Be(0, "loading is not a change");
    }

    [Test]
    public async Task Everything_waits_for_the_load_so_a_request_during_startup_never_sees_an_empty_state()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var agentId = await SeedAgentAsync(context);
        var live = new LiveState(TimeProvider.System, NullLogger<LiveState>.Instance);

        //act
        var agents = live.ListAgentsAsync(CancellationToken.None);
        var registering = live.RegisterAgentAsync(null, "late", [], "1", null, CancellationToken.None);
        await Task.Delay(100);

        //assert - nothing is answered, or changed, before the database has been read
        agents.IsCompleted.Should().BeFalse();
        registering.IsCompleted.Should().BeFalse();

        await live.LoadAsync(context, CancellationToken.None);
        (await agents).Select(agent => agent.Id).Should().Contain(agentId, "what was in the database is there");
        await registering;
    }

    [Test]
    public async Task Every_change_is_an_event_applied_and_queued_in_the_order_it_happened()
    {
        //arrange
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = await TestLiveState.CreateAsync(clock: clock);
        var pipelineId = Guid.NewGuid();

        //act
        var agentId = await live.RegisterAgentAsync(null, "a", ["linux"], "1.0", """{"x":1}""", CancellationToken.None);
        var runId = await live.CreateRunAsync(pipelineId, "p", "Pkg", "2.0", agentId, ["read", "load"], CancellationToken.None);
        await live.StartRunAsync(runId, CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "read", 0, 5, 0, 10, null, null), CancellationToken.None);
        await live.ResourceSampledAsync(runId, agentId, 12.5, 2048, CancellationToken.None);
        await live.AgentHeartbeatAsync(agentId, CancellationToken.None);
        await live.FinishRunAsync(runId, "Succeeded", 0, 5, 5, 0, CancellationToken.None);

        //assert
        var events = live.DrainPendingEvents();
        events.Select(e => e.GetType()).Should().Equal(
            typeof(AgentRegisteredEvent), typeof(RunCreatedEvent), typeof(RunStartedEvent), typeof(StageCompletedEvent),
            typeof(ResourceSampledEvent), typeof(AgentHeartbeatEvent), typeof(RunFinishedEvent));
        live.PendingEventCount.Should().Be(0, "draining takes them");

        var run = live.TryGetRunSummary(runId)!;
        (run.Status, run.StagesCompleted, run.StagesExpected, run.RowsWritten).Should().Be(("Succeeded", 1, 2, 5L));
        run.AgentName.Should().Be("a");
        live.TryGetAgent(agentId)!.Tags.Should().Equal("linux");
    }

    [Test]
    public async Task Starting_a_run_that_is_already_running_changes_nothing_and_an_unknown_run_is_reported()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var runId = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", null, [], CancellationToken.None);

        //act
        (await live.StartRunAsync(runId, CancellationToken.None)).Should().BeTrue();
        var afterFirst = live.PendingEventCount;
        (await live.StartRunAsync(runId, CancellationToken.None)).Should().BeTrue("it exists, so the report is fine");
        var unknown = await live.StartRunAsync(Guid.NewGuid(), CancellationToken.None);

        //assert
        live.PendingEventCount.Should().Be(afterFirst, "the second start found nothing to change");
        unknown.Should().BeFalse();
        live.TryGetRunSummary(runId)!.Status.Should().Be("Running");
    }

    [Test]
    public async Task Reads_come_from_memory_so_a_change_made_to_the_database_behind_its_back_is_not_seen()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var agentId = await SeedAgentAsync(context);
        var runId = await SeedRunAsync(context, pipelineId, agentId, "Running");
        var live = await TestLiveState.CreateAsync(context);

        //act
        context.StageResults.Add(new StageResult { Id = Guid.NewGuid(), RunId = runId, Sequence = 0, Name = "read", ElapsedMs = 1 });
        (await context.Agents.SingleAsync(a => a.Id == agentId)).Status = "Offline";
        await context.SaveChangesAsync();

        //assert
        (await live.GetRunAsync(context, runId, CancellationToken.None))!.Stages.Should().BeEmpty();
        (await live.ListAgentsAsync(CancellationToken.None)).Single().Status.Should().Be("Online");
    }

    [Test]
    public async Task A_stage_replaces_the_one_held_for_the_same_sequence_and_samples_keep_only_the_newest()
    {
        //arrange
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = await TestLiveState.CreateAsync(clock: clock);
        var runId = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", null, [], CancellationToken.None);

        //act
        await live.StageCompletedAsync(runId, new StageDto(1, "transform", 5, 5, 0, 10, null, null), CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "read", 0, 5, 0, 10, null, null), CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "read", 0, 6, 0, 11, null, null), CancellationToken.None);
        for (var i = 0; i < MaxSamplesHeld + 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await live.ResourceSampledAsync(runId, Guid.NewGuid(), i, i, CancellationToken.None);
        }

        // a sample reported at the very same instant as the last one is the same sample
        await live.ResourceSampledAsync(runId, Guid.NewGuid(), 999, 999, CancellationToken.None);

        //assert
        var detail = live.TryGetRun(runId)!;
        detail.Stages.Select(s => (s.Sequence, s.RowsOut)).Should().Equal((0, 6), (1, 5));
        detail.Run.StagesCompleted.Should().Be(2);
        detail.ResourceSamples.Should().HaveCount(MaxSamplesHeld);
        detail.ResourceSamples[0].CpuPercent.Should().Be(10, "the ten oldest were dropped");
        detail.ResourceSamples[^1].CpuPercent.Should().Be(MaxSamplesHeld + 9, "and the repeat of the last timestamp was ignored");
        detail.ResourceSamples.Select(s => s.SampledAtUtc).Should().BeInAscendingOrder();
    }

    [Test]
    public async Task A_burst_of_changes_to_one_run_is_one_notice_for_the_run_and_one_for_its_agent()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var agentId = await live.RegisterAgentAsync(null, "a", [], "1", null, CancellationToken.None);
        var runId = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", agentId, [], CancellationToken.None);
        var other = await live.CreateRunAsync(Guid.NewGuid(), "q", "Pkg", "1", null, [], CancellationToken.None);

        using var everything = live.Subscribe();
        using var onlyThisRun = live.Subscribe(change => change is { Kind: LiveChangeKind.Run } && change.Id == runId);
        using var onlyTheOtherRun = live.Subscribe(change => change is { Kind: LiveChangeKind.Run } && change.Id == other);

        //act
        for (var i = 0; i < 5; i++)
        {
            await live.StageCompletedAsync(runId, new StageDto(i, $"s{i}", 0, 0, 0, 0, null, null), CancellationToken.None);
        }

        //assert
        (await everything.NextAsync(Brief, CancellationToken.None)).Should().BeEquivalentTo(
            [new LiveChange(LiveChangeKind.Run, runId), new LiveChange(LiveChangeKind.Agent, agentId)]);
        (await onlyThisRun.NextAsync(Brief, CancellationToken.None)).Should().Equal(new LiveChange(LiveChangeKind.Run, runId));
        (await onlyTheOtherRun.NextAsync(Brief, CancellationToken.None)).Should().BeEmpty();
        (await everything.NextAsync(Brief, CancellationToken.None)).Should().BeEmpty("nothing is repeated once it has been read");
    }

    [Test]
    public async Task A_run_finishing_is_reflected_for_its_agent_and_runs_past_retention_are_evicted()
    {
        //arrange
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = await TestLiveState.CreateAsync(clock: clock);
        var agentId = await live.RegisterAgentAsync(null, "a", [], "1", null, CancellationToken.None);
        var first = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", agentId, [], CancellationToken.None);
        var second = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", agentId, [], CancellationToken.None);
        (await live.ListAgentsAsync(CancellationToken.None)).Single().ActiveRuns.Should().HaveCount(2);

        // The first run finishes...
        await live.FinishRunAsync(first, "Succeeded", 0, 1, 1, 0, CancellationToken.None);
        (await live.ListAgentsAsync(CancellationToken.None)).Single().ActiveRuns.Select(r => r.Id).Should().Equal(second);

        //act - ...and by the time the second one does, it is long past retention
        clock.Advance(LiveState.Retention + TimeSpan.FromMinutes(1));
        await live.FinishRunAsync(second, "Failed", 1, 0, 0, 0, CancellationToken.None);

        //assert
        live.TryGetRun(first).Should().BeNull("an hour after it finished");
        live.TryGetRun(second).Should().NotBeNull();
        live.DrainPendingEvents().OfType<RunFinishedEvent>().Should().HaveCount(2, "eviction from memory never drops what is still to be written");
    }

    [Test]
    public async Task An_agent_registering_heartbeating_and_going_silent_is_tracked_with_its_uptime()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(now);
        var live = await TestLiveState.CreateAsync(clock: clock);

        //act + assert
        var agentId = await live.RegisterAgentAsync(null, "a", [], "1", $$"""{"startedAtUtc":"{{now.AddHours(-1):O}}"}""", CancellationToken.None);
        live.TryGetAgent(agentId)!.UptimeSeconds.Should().BeInRange(3599, 3601);

        clock.Advance(TimeSpan.FromSeconds(31));
        var sweep = await live.SweepAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        sweep.Should().Be(new LiveSweepResult(1, 0));
        live.TryGetAgent(agentId)!.Status.Should().Be("Offline");
        live.TryGetAgent(agentId)!.UptimeSeconds.Should().BeNull();

        (await live.AgentHeartbeatAsync(agentId, CancellationToken.None)).Should().BeTrue();
        live.TryGetAgent(agentId)!.Status.Should().Be("Online");
        live.TryGetAgent(agentId)!.LastHeartbeatAtUtc.Should().Be(clock.GetUtcNow().UtcDateTime);

        (await live.AgentHeartbeatAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeFalse("unknown agents are left alone");
    }

    [Test]
    public async Task Runs_listed_are_memorys_merged_with_the_databases_with_memory_winning()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var agentId = await SeedAgentAsync(context);
        var live = await TestLiveState.CreateAsync(context, new ManualTimeProvider(now));

        // The database is a few seconds behind: it still has a run the state has already finished, and
        // does not have one the state finished that was never written yet.
        var finishedInMemory = await SeedRunAsync(context, pipelineId, agentId, "Running", requested: now.UtcDateTime.AddMinutes(-10));
        await live.LoadAsync(context, CancellationToken.None);
        await live.FinishRunAsync(finishedInMemory, "Succeeded", 0, 1, 1, 0, CancellationToken.None);
        var onlyInMemory = await live.CreateRunAsync(pipelineId, "p", "Pkg", "1", agentId, [], CancellationToken.None);
        await live.FinishRunAsync(onlyInMemory, "Failed", 1, 0, 0, 0, CancellationToken.None);
        var old = await SeedRunAsync(context, pipelineId, agentId, "Succeeded", requested: now.UtcDateTime.AddDays(-2), completed: now.UtcDateTime.AddDays(-2));

        //act
        var finished = await live.ListRunsAsync(context, active: false, null, 50, CancellationToken.None);
        var all = await live.ListRunsAsync(context, active: null, null, 50, CancellationToken.None);
        var active = await live.ListRunsAsync(context, active: true, null, 50, CancellationToken.None);

        //assert
        finished.Select(r => (r.Id, r.Status)).Should().BeEquivalentTo(
            [(onlyInMemory, "Failed"), (finishedInMemory, "Succeeded"), (old, "Succeeded")]);
        all.Should().HaveCount(3).And.OnlyHaveUniqueItems(r => r.Id);
        all.Single(r => r.Id == finishedInMemory).Status.Should().Be("Succeeded", "memory is ahead of the database");
        active.Should().BeEmpty();
        finished.Select(r => r.RequestedAtUtc).Should().BeInDescendingOrder();
    }

    [Test]
    public async Task A_new_runs_expected_stages_are_those_of_the_last_successful_run_memory_first()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var old = await SeedRunAsync(context, pipelineId, null, "Succeeded", requested: DateTime.UtcNow.AddDays(-3), completed: DateTime.UtcNow.AddDays(-3));
        context.StageResults.Add(new StageResult { Id = Guid.NewGuid(), RunId = old, Sequence = 0, Name = "db-stage" });
        await context.SaveChangesAsync();
        var live = await TestLiveState.CreateAsync(context);

        // from the database while memory has no such run
        (await live.ExpectedStagesAsync(context, pipelineId, CancellationToken.None)).Should().Equal("db-stage");

        //act - then one finishes in memory, not yet written
        var runId = await live.CreateRunAsync(pipelineId, "p", "Pkg", "1", null, [], CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "mem-a", 0, 0, 0, 0, null, null), CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(1, "mem-b", 0, 0, 0, 0, null, null), CancellationToken.None);
        await live.FinishRunAsync(runId, "Succeeded", 0, 0, 0, 0, CancellationToken.None);

        //assert
        (await live.ExpectedStagesAsync(context, pipelineId, CancellationToken.None)).Should().Equal("mem-a", "mem-b");
    }

    [Test]
    public async Task Only_an_agent_that_has_stopped_heartbeating_can_be_removed()
    {
        //arrange - "gone" stops heartbeating; "online" keeps going
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = await TestLiveState.CreateAsync(clock: clock);
        var online = await live.RegisterAgentAsync(null, "online", [], "1", null, CancellationToken.None);
        var gone = await live.RegisterAgentAsync(null, "gone", [], "1", null, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await live.AgentHeartbeatAsync(online, CancellationToken.None);
        await live.SweepAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        live.DrainPendingEvents();
        using var changes = live.Subscribe(change => change.Kind == LiveChangeKind.AgentRemoved);

        //act + assert
        (await live.RemoveAgentAsync(online, CancellationToken.None)).Should().Be(AgentRemovalResult.StillOnline);
        (await live.RemoveAgentAsync(Guid.NewGuid(), CancellationToken.None)).Should().Be(AgentRemovalResult.NotFound);
        live.PendingEventCount.Should().Be(0, "a refusal is not an event");

        (await live.RemoveAgentAsync(gone, CancellationToken.None)).Should().Be(AgentRemovalResult.Removed);
        live.TryGetAgent(gone).Should().BeNull();
        (await live.ListAgentsAsync(CancellationToken.None)).Select(a => a.MachineName).Should().Equal("online");
        live.DrainPendingEvents().Should().ContainSingle().Which.Should().BeOfType<AgentRemovedEvent>();
        (await changes.NextAsync(Brief, CancellationToken.None)).Should().Equal(new LiveChange(LiveChangeKind.AgentRemoved, gone));
    }

    [Test]
    public async Task An_offline_agent_that_still_has_a_run_in_flight_is_not_removed()
    {
        //arrange - the sweep marks it offline and its runs lost, so make a run in flight afterwards
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = await TestLiveState.CreateAsync(clock: clock);
        var agentId = await live.RegisterAgentAsync(null, "a", [], "1", null, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await live.SweepAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        live.TryGetAgent(agentId)!.Status.Should().Be("Offline");
        await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", agentId, [], CancellationToken.None);

        //act
        var result = await live.RemoveAgentAsync(agentId, CancellationToken.None);

        //assert
        result.Should().Be(AgentRemovalResult.HasActiveRuns);
        live.TryGetAgent(agentId).Should().NotBeNull();
    }

    [Test]
    public async Task A_removed_agent_is_not_loaded_again_after_a_restart()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var kept = await SeedAgentAsync(context, "kept");
        var removed = await SeedAgentAsync(context, "removed");
        (await context.Agents.SingleAsync(a => a.Id == removed)).Status = "Removed";
        await context.SaveChangesAsync();

        //act
        var live = await TestLiveState.CreateAsync(context);

        //assert
        (await live.ListAgentsAsync(CancellationToken.None)).Select(a => a.Id).Should().Equal(kept);
    }

    [Test]
    public async Task Clearing_a_pipelines_history_drops_its_finished_runs_from_memory_and_keeps_the_rest()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var pipeline = Guid.NewGuid();
        var other = Guid.NewGuid();
        var finished1 = await live.CreateRunAsync(pipeline, "p", "Pkg", "1", null, [], CancellationToken.None);
        var finished2 = await live.CreateRunAsync(pipeline, "p", "Pkg", "1", null, [], CancellationToken.None);
        var running = await live.CreateRunAsync(pipeline, "p", "Pkg", "1", null, [], CancellationToken.None);
        var elsewhere = await live.CreateRunAsync(other, "q", "Pkg", "1", null, [], CancellationToken.None);
        await live.FinishRunAsync(finished1, "Succeeded", 0, 1, 1, 0, CancellationToken.None);
        await live.FinishRunAsync(finished2, "Failed", 1, 0, 0, 0, CancellationToken.None);
        await live.FinishRunAsync(elsewhere, "Succeeded", 0, 1, 1, 0, CancellationToken.None);
        using var changes = live.Subscribe(change => change.Kind == LiveChangeKind.Pipelines);

        //act
        await live.ClearHistoryAsync(pipeline, CancellationToken.None);

        //assert
        live.TryGetRun(finished1).Should().BeNull();
        live.TryGetRun(finished2).Should().BeNull();
        live.TryGetRun(running).Should().NotBeNull("a run in flight is not history");
        live.TryGetRun(elsewhere).Should().NotBeNull("another pipeline's history is left alone");
        live.DrainPendingEvents().Last().Should().BeOfType<PipelineHistoryClearedEvent>().Which.PipelineId.Should().Be(pipeline);
        (await changes.NextAsync(Brief, CancellationToken.None)).Should().NotBeEmpty();
    }

    [Test]
    public async Task What_the_database_still_has_of_a_cleared_history_stays_hidden_until_the_clear_is_written()
    {
        //arrange - an old finished run only the database has
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedPipelineAsync(context);
        var old = await SeedRunAsync(context, pipelineId, null, "Succeeded", requested: DateTime.UtcNow.AddDays(-3), completed: DateTime.UtcNow.AddDays(-3));
        var live = await TestLiveState.CreateAsync(context);
        (await live.GetRunAsync(context, old, CancellationToken.None)).Should().NotBeNull();

        //act
        await live.ClearHistoryAsync(pipelineId, CancellationToken.None);

        //assert - gone from every read, though the row is still there
        (await live.ListRunsAsync(context, false, pipelineId, 50, CancellationToken.None)).Should().BeEmpty();
        (await live.GetRunAsync(context, old, CancellationToken.None)).Should().BeNull();
        (await live.LastRunAsync(context, pipelineId, CancellationToken.None)).Should().BeNull();
        (await context.Runs.CountAsync()).Should().Be(1);

        // once the clear is written the rows really are gone, and nothing is being hidden any more
        var events = live.DrainPendingEvents();
        await LiveEventWriter.WriteAsync(context, events, CancellationToken.None);
        live.MarkWritten(events);
        (await context.Runs.CountAsync()).Should().Be(0);

        var again = await live.CreateRunAsync(pipelineId, "p", "Pkg", "1", null, [], CancellationToken.None);
        await live.FinishRunAsync(again, "Succeeded", 0, 0, 0, 0, CancellationToken.None);
        (await live.ListRunsAsync(context, false, pipelineId, 50, CancellationToken.None)).Select(r => r.Id).Should().Equal(again);
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

    [Test]
    public async Task An_agent_that_registers_again_with_its_id_is_the_same_agent_with_its_tags_kept()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var first = await live.RegisterAgentAsync(null, "container-1", ["linux"], "1", null, CancellationToken.None);
        await live.SetAgentTagsAsync(first, ["gpu"], CancellationToken.None);
        using var changes = live.Subscribe();

        //act
        var second = await live.RegisterAgentAsync(first, "container-2", ["linux", "arm"], "2", null, CancellationToken.None);

        //assert
        second.Should().Be(first);
        var agents = await live.ListAgentsAsync(CancellationToken.None);
        var agent = agents.Should().ContainSingle().Subject;
        (agent.MachineName, agent.Version, agent.Status).Should().Be(("container-2", "2", "Online"));
        agent.AssignedTags.Should().Equal("gpu");
        agent.Tags.Should().BeEquivalentTo(["linux", "arm", "gpu"], "what it reports plus what a user gave it");
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Contain(new LiveChange(LiveChangeKind.Agent, first));
    }

    [Test]
    public async Task An_agent_that_does_not_bring_an_id_gets_a_new_one_each_time()
    {
        var live = await TestLiveState.CreateAsync();

        var first = await live.RegisterAgentAsync(null, "a", [], "1", null, CancellationToken.None);
        var second = await live.RegisterAgentAsync(null, "a", [], "1", null, CancellationToken.None);

        second.Should().NotBe(first);
        (await live.ListAgentsAsync(CancellationToken.None)).Should().HaveCount(2);
    }

    [Test]
    public async Task Tags_given_to_an_agent_are_listed_with_the_ones_it_reports_and_announced_and_queued_for_the_database()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var agentId = await live.RegisterAgentAsync(null, "a", ["linux", "GPU"], "1", null, CancellationToken.None);
        using var changes = live.Subscribe();
        var pending = live.PendingEventCount;

        //act
        var set = await live.SetAgentTagsAsync(agentId, ["gpu", "eu"], CancellationToken.None);
        var unknown = await live.SetAgentTagsAsync(Guid.NewGuid(), ["x"], CancellationToken.None);

        //assert
        set.Should().BeTrue();
        unknown.Should().BeFalse();
        var agent = live.TryGetAgent(agentId)!;
        agent.AssignedTags.Should().Equal("gpu", "eu");
        agent.Tags.Should().Equal(["linux", "GPU", "eu"], "the same tag in a different case is one tag");
        live.PendingEventCount.Should().Be(pending + 1);
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Contain(new LiveChange(LiveChangeKind.Agent, agentId));
    }

    [Test]
    public async Task What_a_user_gave_an_agent_comes_back_after_a_restart_and_goes_when_it_is_removed()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var live = await TestLiveState.CreateAsync();
        var kept = await live.RegisterAgentAsync(null, "kept", [], "1", null, CancellationToken.None);
        var removed = await live.RegisterAgentAsync(null, "removed", [], "1", null, CancellationToken.None);
        await live.SetAgentTagsAsync(kept, ["gpu"], CancellationToken.None);
        await live.SetAgentTagsAsync(removed, ["gpu"], CancellationToken.None);
        await live.SweepAsync(TimeSpan.Zero, CancellationToken.None);
        await live.RemoveAgentAsync(removed, CancellationToken.None);

        //act - what is queued is written; a restarted server loads it
        await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(context, live.DrainPendingEvents(), CancellationToken.None));
        var restarted = await database.ReadAsync(async context => await TestLiveState.CreateAsync(context));

        //assert
        restarted.TryGetAgent(kept)!.AssignedTags.Should().Equal("gpu");
        restarted.TryGetAgent(removed).Should().BeNull();

        // and a removed agent that comes back is a fresh start
        await restarted.RegisterAgentAsync(removed, "removed", [], "1", null, CancellationToken.None);
        restarted.TryGetAgent(removed)!.AssignedTags.Should().BeEmpty();
    }

    [Test]
    public async Task The_load_of_each_agent_is_its_tags_and_the_runs_it_has_going()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var busy = await live.RegisterAgentAsync(null, "busy", ["linux"], "1", null, CancellationToken.None);
        var idle = await live.RegisterAgentAsync(null, "idle", [], "1", null, CancellationToken.None);
        await live.SetAgentTagsAsync(idle, ["gpu"], CancellationToken.None);
        await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", busy, [], CancellationToken.None);
        var done = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1", busy, [], CancellationToken.None);
        await live.FinishRunAsync(done, "Succeeded", 0, 1, 1, 0, CancellationToken.None);

        //act
        var loads = (await live.AgentLoadsAsync(CancellationToken.None)).ToDictionary(load => load.Id);

        //assert
        loads[busy].Should().BeEquivalentTo(new AgentLoad(busy, ["linux"], 1));
        loads[idle].Should().BeEquivalentTo(new AgentLoad(idle, ["gpu"], 0));
        live.IsKnownAgent(busy).Should().BeTrue();
        live.IsKnownAgent(Guid.NewGuid()).Should().BeFalse();
    }

    private static async Task<Guid> SeedAgentAsync(ServerDbContext context, string name = "agent-01")
    {
        var agent = new Database.Entities.Agent
        {
            Id = Guid.NewGuid(), MachineName = name, Tags = [], AssignedTags = [], Version = "1.0.0", Status = "Online", LastHeartbeatAt = DateTime.UtcNow,
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
