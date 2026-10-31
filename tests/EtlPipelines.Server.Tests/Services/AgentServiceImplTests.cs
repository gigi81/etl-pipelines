using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace EtlPipelines.Server.Tests.Services;

/// <summary><see cref="AgentServiceImpl"/> keeping <see cref="LiveState"/> up to date as agents call in.</summary>
[Category("Server")]
public class AgentServiceImplTests
{
    [Test]
    public async Task Registering_and_heartbeating_show_up_in_the_live_state_and_notify_listeners()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var live = new LiveState(TimeProvider.System, NullLogger<LiveState>.Instance);
        var service = Create(context, live);
        using var changes = live.Subscribe();

        //act
        var registered = await service.RegisterAgent(new RegisterAgentRequest { MachineName = "agent-01", Version = "1.0.0" }, CallContext());
        var agentId = Guid.Parse(registered.AgentId);

        //assert
        live.TryGetAgent(agentId)!.MachineName.Should().Be("agent-01");
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Contain(new LiveChange(LiveChangeKind.Agent, agentId));

        // it was marked offline, then heartbeats again
        await live.AgentsOfflineAsync(context, [agentId], CancellationToken.None);
        await service.Heartbeat(new AgentHeartbeatRequest { AgentId = registered.AgentId }, CallContext());
        live.TryGetAgent(agentId)!.Status.Should().Be("Online");
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().Contain(new LiveChange(LiveChangeKind.Agent, agentId));
    }

    [Test]
    public async Task What_an_agent_reports_about_a_run_updates_it_in_the_live_state()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var live = new LiveState(TimeProvider.System, NullLogger<LiveState>.Instance);
        var service = Create(context, live);
        var agentId = Guid.Parse((await service.RegisterAgent(new RegisterAgentRequest { MachineName = "a", Version = "1" }, CallContext())).AgentId);

        var package = new Package { Id = Guid.NewGuid(), NugetPackageId = "Pkg", CreatedAt = DateTime.UtcNow };
        var version = new PackageVersion { Id = Guid.NewGuid(), PackageId = package.Id, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" };
        var pipeline = new Pipeline { Id = Guid.NewGuid(), PackageVersionId = version.Id, Name = "p", CreatedAt = DateTime.UtcNow };
        var run = new Run { Id = Guid.NewGuid(), PipelineId = pipeline.Id, AgentId = agentId, Status = "Dispatched", RequestedAt = DateTime.UtcNow };
        context.AddRange(package, version, pipeline, run);
        await context.SaveChangesAsync();
        await live.RunCreatedAsync(context, run.Id, CancellationToken.None);

        //act + assert
        await service.ReportExecutionStatus(Report(agentId, run.Id, ReportExecutionStatusRequest.Types.Status.Started), CallContext());
        live.TryGetRunSummary(run.Id)!.Status.Should().Be("Running");

        var sample = Report(agentId, run.Id, ReportExecutionStatusRequest.Types.Status.ResourceSample);
        sample.CpuPercent = 42;
        sample.WorkingSetBytes = 4096;
        await service.ReportExecutionStatus(sample, CallContext());
        live.TryGetRun(run.Id)!.ResourceSamples.Should().ContainSingle(s => s.CpuPercent == 42 && s.WorkingSetBytes == 4096);

        var exited = Report(agentId, run.Id, ReportExecutionStatusRequest.Types.Status.Exited);
        exited.ExitCode = 1;
        await service.ReportExecutionStatus(exited, CallContext());
        live.TryGetRunSummary(run.Id)!.Status.Should().Be("Failed");
        live.TryGetAgent(agentId)!.ActiveRuns.Should().BeEmpty();
    }

    [Test]
    public async Task A_successful_install_tells_listeners_the_installed_pipelines_changed()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var live = new LiveState(TimeProvider.System, NullLogger<LiveState>.Instance);
        var connections = new AgentConnectionRegistry();
        var service = Create(context, live, connections);
        using var changes = live.Subscribe(change => change.Kind == LiveChangeKind.Pipelines);

        // An install the Server dispatched and is waiting on.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<WorkItem>();
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

    private static AgentServiceImpl Create(ServerDbContext context, LiveState live, AgentConnectionRegistry? connections = null) =>
        new(
            context,
            connections ?? new AgentConnectionRegistry(),
            new PackageCatalogService(context, Mock.Of<INuGetFeedClient>()),
            new RunStatusStore(),
            live);

    private static ServerCallContext CallContext()
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context.Object;
    }
}
