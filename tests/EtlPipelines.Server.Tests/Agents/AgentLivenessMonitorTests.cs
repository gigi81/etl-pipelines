using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Tests.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Server.Tests.Agents;

/// <summary>
/// Fast tests for <see cref="AgentLivenessMonitor.SweepAsync"/> - SERVER.md Phase 8's own
/// instruction ("fast tests for the heartbeat timeout (fake clock)"). The sweep works on
/// <see cref="LiveState"/>, which knows every agent's last heartbeat, so it needs no database: the
/// state is loaded from a seeded one and a clock the test moves stands in for a real 30 seconds.
/// </summary>
[Category("Server")]
public class AgentLivenessMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task An_agent_past_the_timeout_is_marked_Offline()
    {
        //arrange
        var (live, monitor, agentId, _) = await SetUpAsync(heartbeatAge: Timeout + TimeSpan.FromSeconds(1));

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        live.TryGetAgent(agentId)!.Status.Should().Be("Offline");
    }

    [Test]
    public async Task After_the_server_starts_agents_are_given_the_timeout_to_be_heard_from_before_they_are_judged()
    {
        //arrange - an agent last heard from long before the server was (re)started, with a short timeout so the test is quick
        var shortTimeout = TimeSpan.FromMilliseconds(600);
        var (live, _, agentId, _) = await SetUpAsync(heartbeatAge: TimeSpan.FromHours(1));
        var monitor = new AgentLivenessMonitor(
            live,
            Microsoft.Extensions.Options.Options.Create(new AgentLivenessOptions { Timeout = shortTimeout, PollInterval = TimeSpan.FromMilliseconds(100) }),
            TimeProvider.System,
            NullLogger<AgentLivenessMonitor>.Instance);

        //act
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(200);

            //assert - not offline yet: it has until the timeout to reconnect and heartbeat
            live.TryGetAgent(agentId)!.Status.Should().Be("Online");

            // ...and once the timeout has passed without it being heard from, it is
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (live.TryGetAgent(agentId)!.Status != "Offline" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            live.TryGetAgent(agentId)!.Status.Should().Be("Offline");
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task An_agent_within_the_timeout_stays_Online()
    {
        //arrange
        var (live, monitor, agentId, _) = await SetUpAsync(heartbeatAge: Timeout - TimeSpan.FromSeconds(1));

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        live.TryGetAgent(agentId)!.Status.Should().Be("Online");
    }

    [Test]
    [Arguments("Queued")]
    [Arguments("Dispatched")]
    [Arguments("Running")]
    public async Task A_run_still_active_on_a_timed_out_agent_is_marked_AgentLost(string activeStatus)
    {
        //arrange
        var (live, monitor, agentId, runId) = await SetUpAsync(heartbeatAge: Timeout + TimeSpan.FromSeconds(1), runStatus: activeStatus);
        using var changes = live.Subscribe();

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        var run = live.TryGetRunSummary(runId)!;
        run.Status.Should().Be("AgentLost");
        run.CompletedAtUtc.Should().NotBeNull();
        live.TryGetAgent(agentId)!.ActiveRuns.Should().BeEmpty();
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Contain(
            [new LiveChange(LiveChangeKind.Run, runId), new LiveChange(LiveChangeKind.Agent, agentId)]);
    }

    [Test]
    public async Task A_run_already_finished_is_left_alone_even_if_its_agent_timed_out()
    {
        //arrange
        var (live, monitor, _, runId) = await SetUpAsync(
            heartbeatAge: Timeout + TimeSpan.FromSeconds(1), runStatus: "Succeeded", completedMinutesAgo: 1);

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        live.TryGetRunSummary(runId)!.Status.Should().Be("Succeeded");
    }

    [Test]
    public async Task What_a_sweep_changes_is_queued_for_the_database_in_order()
    {
        //arrange
        var (live, monitor, agentId, runId) = await SetUpAsync(heartbeatAge: Timeout + TimeSpan.FromSeconds(1), runStatus: "Running");

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        live.DrainPendingEvents().Select(e => e.GetType().Name).Should().Equal("AgentOfflineEvent", "RunFinishedEvent");
        agentId.Should().NotBeEmpty();
        runId.Should().NotBeEmpty();
    }

    private static async Task<(LiveState Live, AgentLivenessMonitor Monitor, Guid AgentId, Guid RunId)> SetUpAsync(
        TimeSpan heartbeatAge, string runStatus = "Running", int? completedMinutesAgo = null)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(now);
        var context = SqliteServerDbContext.Create();

        var agentId = Guid.NewGuid();
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = agentId, MachineName = "agent-01", Tags = [], AssignedTags = [], Version = "1.0.0", Status = "Online", LastHeartbeatAt = now.UtcDateTime - heartbeatAge,
        });

        var runId = Guid.NewGuid();
        var pipelineId = SeedPipeline(context);
        context.Runs.Add(new Run
        {
            Id = runId,
            PipelineId = pipelineId,
            AgentId = agentId,
            Status = runStatus,
            RequestedAt = now.UtcDateTime.AddMinutes(-5),
            CompletedAt = completedMinutesAgo is { } minutes ? now.UtcDateTime.AddMinutes(-minutes) : null,
        });
        await context.SaveChangesAsync();

        var live = await TestLiveState.CreateAsync(context, clock);
        var monitor = new AgentLivenessMonitor(
            live,
            Microsoft.Extensions.Options.Options.Create(new AgentLivenessOptions { Timeout = Timeout, PollInterval = TimeSpan.FromSeconds(10) }),
            clock,
            NullLogger<AgentLivenessMonitor>.Instance);

        return (live, monitor, agentId, runId);
    }

    /// <summary>A minimal Package/PackageVersion/Pipeline chain - a run needs a real pipeline to attach to.</summary>
    private static Guid SeedPipeline(ServerDbContext context)
    {
        var package = new Package { Id = Guid.NewGuid(), NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow };
        var version = new PackageVersion { Id = Guid.NewGuid(), PackageId = package.Id, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" };
        var pipeline = new Pipeline { Id = Guid.NewGuid(), PackageVersionId = version.Id, Name = "csv-to-database", CreatedAt = DateTime.UtcNow };
        context.AddRange(package, version, pipeline);
        return pipeline.Id;
    }
}
