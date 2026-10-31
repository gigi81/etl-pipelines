using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Live.Events;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Tests.Live;

/// <summary>
/// <see cref="LiveEventWriter"/>: replaying events, in order, into the tables - so a batch holds the
/// whole history of what happened in it, not just the last state of each thing.
/// </summary>
[Category("Server")]
public class LiveEventWriterTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task A_runs_whole_life_inside_one_batch_is_written_complete_with_every_intermediate_state()
    {
        //arrange - it is created, started, reports two stages and samples, and finishes, all before one sync
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync();
        var agentId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        LiveEvent[] events =
        [
            new AgentRegisteredEvent(agentId, "agent-01", ["linux"], "1.0", """{"os":"x"}""", T0),
            new RunCreatedEvent(runId, pipelineId, "p", "Pkg", "1.0.0", agentId, [], T0.AddSeconds(1)),
            new RunStartedEvent(runId, T0.AddSeconds(2)),
            new StageCompletedEvent(runId, Guid.NewGuid(), new StageDto(0, "read", 0, 5, 0, 100, null, null), T0.AddSeconds(3)),
            new ResourceSampledEvent(runId, agentId, Guid.NewGuid(), 10, 1000, T0.AddSeconds(4)),
            new StageCompletedEvent(runId, Guid.NewGuid(), new StageDto(1, "load", 5, 5, 0, 200, null, null), T0.AddSeconds(5)),
            new ResourceSampledEvent(runId, agentId, Guid.NewGuid(), 20, 2000, T0.AddSeconds(6)),
            new RunFinishedEvent(runId, "Succeeded", 0, 5, 5, 0, T0.AddSeconds(7)),
        ];

        //act
        var skipped = await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(context, events, CancellationToken.None));

        //assert
        skipped.Should().Be(0);
        await database.ReadAsync(async context =>
        {
            var agent = await context.Agents.SingleAsync();
            (agent.MachineName, agent.Status, agent.Info).Should().Be(("agent-01", "Online", """{"os":"x"}"""));
            agent.Tags.Should().Equal("linux");

            var run = await context.Runs.SingleAsync();
            run.Status.Should().Be("Succeeded");
            run.AgentId.Should().Be(agentId);
            Utc(run.RequestedAt).Should().Be(T0.AddSeconds(1), "when it was requested");
            Utc(run.StartedAt!.Value).Should().Be(T0.AddSeconds(2), "the Running state is kept, as its start time");
            Utc(run.CompletedAt!.Value).Should().Be(T0.AddSeconds(7), "when it finished - not when it was synced");
            (run.ExitCode, run.RowsRead, run.RowsWritten, run.RowsFailed).Should().Be((0, 5L, 5L, 0L));

            (await context.StageResults.OrderBy(s => s.Sequence).Select(s => s.Name).ToListAsync()).Should().Equal("read", "load");
            (await context.AgentResourceSamples.OrderBy(s => s.CpuPercent).Select(s => s.CpuPercent).ToListAsync()).Should().Equal(10, 20);
            return 0;
        });
    }

    [Test]
    public async Task Events_are_replayed_in_order_so_a_thing_that_changes_several_times_ends_where_it_ended()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var agentId = Guid.NewGuid();
        LiveEvent[] events =
        [
            new AgentRegisteredEvent(agentId, "a", [], "1", null, T0),
            new AgentOfflineEvent(agentId, T0.AddSeconds(30)),
            new AgentHeartbeatEvent(agentId, T0.AddSeconds(40)),
            new AgentHeartbeatEvent(agentId, T0.AddSeconds(50)),
        ];

        //act
        await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(context, events, CancellationToken.None));

        //assert
        var agent = await database.ReadAsync(context => context.Agents.SingleAsync());
        agent.Status.Should().Be("Online", "the heartbeats came after it went offline");
        Utc(agent.LastHeartbeatAt).Should().Be(T0.AddSeconds(50));
    }

    [Test]
    public async Task A_later_batch_updates_the_rows_an_earlier_one_wrote()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync();
        var runId = Guid.NewGuid();
        await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(
            context, [new RunCreatedEvent(runId, pipelineId, "p", "Pkg", "1", null, [], T0)], CancellationToken.None));

        //act
        await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(
            context,
            [
                new RunStartedEvent(runId, T0.AddSeconds(1)),
                new StageCompletedEvent(runId, Guid.NewGuid(), new StageDto(0, "read", 0, 1, 0, 1, null, null), T0.AddSeconds(2)),
                new RunFinishedEvent(runId, "Failed", 3, null, null, null, T0.AddSeconds(3)),
            ],
            CancellationToken.None));

        //assert
        var run = await database.ReadAsync(context => context.Runs.SingleAsync());
        (run.Status, run.ExitCode, run.RowsRead).Should().Be(("Failed", 3, null), "an unset count in the event leaves the row's as it is");
        Utc(run.StartedAt!.Value).Should().Be(T0.AddSeconds(1));
        (await database.ReadAsync(context => context.StageResults.CountAsync())).Should().Be(1);
    }

    [Test]
    public async Task A_stage_reported_twice_for_the_same_sequence_is_one_row_with_the_latest_values()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync();
        var runId = Guid.NewGuid();
        await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(
            context,
            [
                new RunCreatedEvent(runId, pipelineId, "p", "Pkg", "1", null, [], T0),
                new StageCompletedEvent(runId, Guid.NewGuid(), new StageDto(0, "read", 0, 1, 0, 1, null, null), T0),
            ],
            CancellationToken.None));

        //act - once in a later batch, once more in the same batch
        await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(
            context,
            [
                new StageCompletedEvent(runId, Guid.NewGuid(), new StageDto(0, "read", 0, 2, 0, 2, null, null), T0),
                new StageCompletedEvent(runId, Guid.NewGuid(), new StageDto(0, "read", 0, 3, 0, 3, "E", "boom"), T0),
            ],
            CancellationToken.None));

        //assert
        var stage = await database.ReadAsync(context => context.StageResults.SingleAsync());
        (stage.RowsOut, stage.ElapsedMs, stage.ErrorCode, stage.ErrorDescription).Should().Be((3L, 3L, "E", "boom"));
    }

    [Test]
    public async Task Events_about_rows_the_database_does_not_have_are_skipped_and_the_rest_still_written()
    {
        //arrange
        await using var database = await TestDatabase.CreateAsync();
        var agentId = Guid.NewGuid();
        LiveEvent[] events =
        [
            new RunStartedEvent(Guid.NewGuid(), T0),
            new AgentHeartbeatEvent(Guid.NewGuid(), T0),
            new AgentRegisteredEvent(agentId, "a", [], "1", null, T0),
        ];

        //act
        var skipped = await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(context, events, CancellationToken.None));

        //assert
        skipped.Should().Be(2);
        (await database.ReadAsync(context => context.Agents.CountAsync())).Should().Be(1);
    }

    [Test]
    public async Task What_memory_holds_and_what_the_database_holds_after_the_replay_are_the_same()
    {
        //arrange - drive a state with commands, as the gRPC services do
        await using var database = await TestDatabase.CreateAsync();
        var pipelineId = await database.SeedPipelineAsync("csv-to-db");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var live = await TestLiveState.CreateAsync(clock: clock);

        var agentId = await live.RegisterAgentAsync("agent-01", ["a"], "1", """{"k":"v"}""", CancellationToken.None);
        var runId = await live.CreateRunAsync(pipelineId, "csv-to-db", "Pkg", "1.0.0", agentId, [], CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await live.StartRunAsync(runId, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await live.StageCompletedAsync(runId, new StageDto(0, "read", 0, 5, 0, 10, null, null), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await live.ResourceSampledAsync(runId, agentId, 33, 4096, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await live.StageCompletedAsync(runId, new StageDto(1, "load", 5, 4, 1, 20, "E", "bad row"), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await live.FinishRunAsync(runId, "Failed", 2, 5, 4, 1, CancellationToken.None);
        var inMemory = live.TryGetRun(runId)!;

        //act - replay the queued events into the database, then load a second state from it, as a restart would
        await database.ReadAsync(async context => await LiveEventWriter.WriteAsync(context, live.DrainPendingEvents(), CancellationToken.None));
        var restarted = await database.ReadAsync(async context => await TestLiveState.CreateAsync(context, clock));

        //assert
        var reloaded = restarted.TryGetRun(runId)!;
        reloaded.Run.Should().BeEquivalentTo(inMemory.Run, options => options.Using<DateTime>(c => c.Subject.Should().BeCloseTo(c.Expectation, TimeSpan.FromMilliseconds(1))).WhenTypeIs<DateTime>());
        reloaded.Stages.Should().BeEquivalentTo(inMemory.Stages);
        reloaded.ResourceSamples.Select(s => (s.CpuPercent, s.WorkingSetBytes)).Should().Equal(inMemory.ResourceSamples.Select(s => (s.CpuPercent, s.WorkingSetBytes)));
        restarted.TryGetAgent(agentId)!.Info.Should().NotBeNull();
        restarted.TryGetAgent(agentId)!.MachineName.Should().Be("agent-01");
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
