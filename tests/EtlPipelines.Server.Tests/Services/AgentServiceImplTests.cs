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
    public async Task Registering_does_not_wait_for_the_database()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);

        //act
        await service.RegisterAgent(new RegisterAgentRequest { MachineName = "a", Version = "1" }, CallContext());

        //assert
        live.PendingEventCount.Should().Be(1, "just the registration, queued for the database");
    }

    [Test]
    public async Task A_heartbeat_from_an_agent_the_server_does_not_know_is_told_so_and_one_from_a_known_agent_is_answered()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var service = Create(live);
        var known = await service.RegisterAgent(new RegisterAgentRequest { MachineName = "a", Version = "1" }, CallContext());

        //act
        var ack = await service.Heartbeat(new AgentHeartbeatRequest { AgentId = known.AgentId }, CallContext());
        var unknown = async () => await service.Heartbeat(new AgentHeartbeatRequest { AgentId = Guid.NewGuid().ToString() }, CallContext());
        var malformed = async () => await service.Heartbeat(new AgentHeartbeatRequest { AgentId = "nope" }, CallContext());

        //assert - told "not found", so the agent registers again, rather than heartbeating to nobody
        ack.Should().NotBeNull();
        (await unknown.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        (await malformed.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task An_agent_that_reconnects_keeps_its_new_connection_when_the_old_one_ends_afterwards()
    {
        //arrange - the agent subscribes, loses the connection, and subscribes again before the server noticed the first was gone
        var connections = new AgentConnectionRegistry();
        var service = Create(await TestLiveState.CreateAsync(), connections);
        var writer = new Mock<IServerStreamWriter<WorkItem>>();
        using var firstCall = new CancellationTokenSource();
        using var secondCall = new CancellationTokenSource();
        var request = new SubscribeRequest { AgentId = "agent" };

        var first = service.Subscribe(request, writer.Object, CallContext(firstCall.Token));
        await Task.Delay(50);
        var second = service.Subscribe(request, writer.Object, CallContext(secondCall.Token));
        await Task.Delay(50);

        //act - the old call ends (the new one replaced it, which ended it) - or is cancelled by the server finally noticing
        await firstCall.CancelAsync();
        try
        {
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Either way: it is over.
        }

        //assert - the agent is still connected on its new call, and work for it still reaches it
        connections.TryGetAnyConnectedAgentId(out var connected).Should().BeTrue();
        connected.Should().Be("agent");
        var work = new ExecutePipelineWorkItem { SessionId = "s", PackageId = "P", PackageVersion = "1", PipelineName = "p" };
        connections.DispatchExecutePipeline("agent", work);
        await Task.Delay(100);
        writer.Verify(w => w.WriteAsync(It.Is<WorkItem>(item => item.ExecutePipeline.SessionId == "s")), Times.Once);

        // and when the call it has now ends, it is gone
        await secondCall.CancelAsync();
        try
        {
            await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Over.
        }

        connections.TryGetAnyConnectedAgentId(out _).Should().BeFalse();
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

    private static ServerCallContext CallContext(CancellationToken cancellationToken = default)
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(cancellationToken);
        return context.Object;
    }
}
