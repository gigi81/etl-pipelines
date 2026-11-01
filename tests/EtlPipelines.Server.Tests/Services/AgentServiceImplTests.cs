using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Services;
using EtlPipelines.Server.Tests.Live;
using Grpc.Core;
using Moq;
using Moq.Protected;

namespace EtlPipelines.Server.Tests.Services;

/// <summary><see cref="AgentServiceImpl"/> keeping <see cref="LiveState"/> - the store - up to date as agents call in.</summary>
[Category("Server")]
public class AgentServiceImplTests
{
    [Test]
    public async Task Registering_and_heartbeating_show_up_in_the_live_state_and_notify_listeners()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);
        using var changes = live.Subscribe();

        //act
        var registered = await service.RegisterAgent(new RegisterAgentRequest { MachineName = "agent-01", Version = "1.0.0" }, CallContext());
        var agentId = Guid.Parse(registered.AgentId);

        //assert
        live.TryGetAgent(agentId)!.MachineName.Should().Be("agent-01");
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Contain(new LiveChange(LiveChangeKind.Agent, agentId));

        // it was marked offline, then heartbeats again
        await live.SweepAsync(TimeSpan.Zero, CancellationToken.None);
        live.TryGetAgent(agentId)!.Status.Should().Be("Offline");
        await service.Heartbeat(new AgentHeartbeatRequest { AgentId = registered.AgentId }, CallContext());
        live.TryGetAgent(agentId)!.Status.Should().Be("Online");
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Contain(new LiveChange(LiveChangeKind.Agent, agentId));
    }

    [Test]
    public async Task Registering_does_not_wait_for_the_database_and_a_heartbeat_from_an_unknown_agent_is_ignored()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);

        //act
        await service.RegisterAgent(new RegisterAgentRequest { MachineName = "a", Version = "1" }, CallContext());
        var ack = await service.Heartbeat(new AgentHeartbeatRequest { AgentId = Guid.NewGuid().ToString() }, CallContext());

        //assert
        ack.Should().NotBeNull();
        live.PendingEventCount.Should().Be(1, "just the registration, queued for the database");
    }

    [Test]
    public async Task An_agent_that_brings_the_id_it_was_issued_is_the_same_agent_and_one_that_does_not_is_new()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);
        var first = await service.RegisterAgent(new RegisterAgentRequest { MachineName = "container-1", Version = "1" }, CallContext());

        //act - the agent starts again, with the id it kept
        var again = await service.RegisterAgent(
            new RegisterAgentRequest { MachineName = "container-2", Version = "1", AgentId = first.AgentId }, CallContext());
        var other = await service.RegisterAgent(new RegisterAgentRequest { MachineName = "elsewhere", Version = "1" }, CallContext());

        //assert
        again.AgentId.Should().Be(first.AgentId);
        other.AgentId.Should().NotBe(first.AgentId);
        (await live.ListAgentsAsync(CancellationToken.None)).Select(agent => agent.MachineName).Should().BeEquivalentTo("container-2", "elsewhere");
    }

    [Test]
    public async Task An_agent_id_that_is_not_an_id_is_refused()
    {
        var service = Create(await TestLiveState.CreateAsync());

        var act = () => service.RegisterAgent(new RegisterAgentRequest { MachineName = "a", AgentId = "not-a-guid" }, CallContext());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task What_an_agent_reports_about_a_run_updates_it_in_the_live_state()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);
        var agentId = Guid.Parse((await service.RegisterAgent(new RegisterAgentRequest { MachineName = "a", Version = "1" }, CallContext())).AgentId);
        var runId = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1.0.0", agentId, [], CancellationToken.None);

        //act + assert
        await service.ReportExecutionStatus(Report(agentId, runId, ReportExecutionStatusRequest.Types.Status.Started), CallContext());
        live.TryGetRunSummary(runId)!.Status.Should().Be("Running");

        var sample = Report(agentId, runId, ReportExecutionStatusRequest.Types.Status.ResourceSample);
        sample.CpuPercent = 42;
        sample.WorkingSetBytes = 4096;
        await service.ReportExecutionStatus(sample, CallContext());
        live.TryGetRun(runId)!.ResourceSamples.Should().ContainSingle(s => s.CpuPercent == 42 && s.WorkingSetBytes == 4096);

        var exited = Report(agentId, runId, ReportExecutionStatusRequest.Types.Status.Exited);
        exited.ExitCode = 1;
        await service.ReportExecutionStatus(exited, CallContext());
        live.TryGetRunSummary(runId)!.Status.Should().Be("Failed");
        live.TryGetAgent(agentId)!.ActiveRuns.Should().BeEmpty();
    }

    [Test]
    public async Task An_exit_after_the_process_already_reported_its_own_outcome_changes_nothing()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);
        var agentId = Guid.Parse((await service.RegisterAgent(new RegisterAgentRequest { MachineName = "a", Version = "1" }, CallContext())).AgentId);
        var runId = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1.0.0", agentId, [], CancellationToken.None);
        await live.FinishRunAsync(runId, "Succeeded", 0, 5, 5, 0, CancellationToken.None);
        var pending = live.PendingEventCount;

        //act
        var exited = Report(agentId, runId, ReportExecutionStatusRequest.Types.Status.Exited);
        exited.ExitCode = 137;
        await service.ReportExecutionStatus(exited, CallContext());

        //assert
        live.TryGetRunSummary(runId)!.Status.Should().Be("Succeeded");
        live.TryGetRunSummary(runId)!.ExitCode.Should().Be(0);
        live.PendingEventCount.Should().Be(pending);
    }

    [Test]
    public async Task A_report_for_an_unknown_or_malformed_session_is_a_no_op()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);

        //act
        await service.ReportExecutionStatus(
            new ReportExecutionStatusRequest { AgentId = Guid.NewGuid().ToString(), SessionId = "nope", Status = ReportExecutionStatusRequest.Types.Status.Started },
            CallContext());
        await service.ReportExecutionStatus(
            Report(Guid.NewGuid(), Guid.NewGuid(), ReportExecutionStatusRequest.Types.Status.Exited), CallContext());

        //assert
        live.PendingEventCount.Should().Be(0);
    }

    [Test]
    public async Task A_successful_install_tells_listeners_the_installed_pipelines_changed()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var connections = new AgentConnectionRegistry();
        var service = Create(live, connections);
        using var changes = live.Subscribe(change => change.Kind == LiveChangeKind.Pipelines);

        // An install the Server dispatched and is waiting on.
        var channel = Channel.CreateUnbounded<WorkItem>();
        connections.Connect("agent", channel);
        var dispatch = connections.DispatchInstallPackageAsync("agent", "Pkg", "1.0.0", [], TimeSpan.FromSeconds(10), CancellationToken.None);
        var workItem = await channel.Reader.ReadAsync();

        //act
        await service.ReportInstallResult(
            new ReportInstallResultRequest { AgentId = "agent", WorkItemId = workItem.WorkItemId, Succeeded = true }, CallContext());
        await dispatch;

        //assert
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().NotBeEmpty();
    }

    private static ReportExecutionStatusRequest Report(Guid agentId, Guid runId, ReportExecutionStatusRequest.Types.Status status) =>
        new() { AgentId = agentId.ToString(), SessionId = runId.ToString(), Status = status };

    // The catalog the install path records into is the one thing here that is still the database's.
    private static AgentServiceImpl Create(LiveState live, AgentConnectionRegistry? connections = null) =>
        new(
            connections ?? new AgentConnectionRegistry(),
            new PackageCatalogService(SqliteServerDbContext.Create(), Mock.Of<INuGetFeedClient>()),
            live);

    private static ServerCallContext CallContext()
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context.Object;
    }
}
