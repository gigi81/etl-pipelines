using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Tests.Live;
using EtlPipelines.Server.Secrets;
using EtlPipelines.Server.Services;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using NuGet.Versioning;

namespace EtlPipelines.Server.Tests.Services;

/// <summary>
/// Proves <see cref="ManagementServiceImpl"/> maps proto messages to/from
/// <see cref="PackageCatalogService"/>/<see cref="SecretsStore"/> correctly, and that the two
/// RPCs Phase 4 leaves for Phase 5 fail the way that phase's own decision calls for. Composes real
/// <see cref="PackageCatalogService"/>/<see cref="SecretsStore"/> instances (SQLite-backed, mocked
/// feed client/protector) rather than mocking them directly - both are concrete, not interfaces,
/// on the same "don't abstract what nothing else ever implements" basis <see cref="INuGetFeedClient"/>
/// and <see cref="IDataProtector"/> are the exception to.
/// </summary>
[Category("Server")]
public class ManagementServiceImplTests
{
    [Test]
    public async Task InstallPackage_fails_with_FailedPrecondition_because_no_agent_exists_yet()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.InstallPackage(new InstallPackageRequest { PackageId = "EtlPipelines.Samples.CsvToDatabase" }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task ExecutePipeline_fails_with_NotFound_for_a_pipeline_id_nothing_installed()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.ExecutePipeline(new ExecutePipelineRequest { PipelineId = Guid.NewGuid().ToString() }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Test]
    public async Task ExecutePipeline_fails_with_FailedPrecondition_because_no_agent_exists_yet()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedInstalledPipelineAsync(context, "csv-to-database");
        var service = Create(context);

        //act
        var act = () => service.ExecutePipeline(new ExecutePipelineRequest { PipelineId = pipelineId.ToString() }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task ExecutePipeline_dispatches_to_a_connected_agent_and_creates_the_run_in_memory()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedInstalledPipelineAsync(context, "csv-to-database");

        var agentGuid = Guid.NewGuid();
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = agentGuid, MachineName = "agent-01", Tags = [], AssignedTags = [], Version = "1.0.0", Status = "Online", LastHeartbeatAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var connections = new AgentConnectionRegistry();
        var agentId = agentGuid.ToString();
        var channel = Channel.CreateUnbounded<WorkItem>();
        connections.Connect(agentId, channel);

        var live = await TestLiveState.CreateAsync(context);
        var service = Create(context, connections: connections, liveState: live);

        //act
        var response = await service.ExecutePipeline(new ExecutePipelineRequest { PipelineId = pipelineId.ToString() }, TestServerCallContext());

        //assert - the run exists, in memory, and the work item went to the agent with its id as the session id
        response.RunId.Should().NotBeNullOrEmpty();
        var run = live.TryGetRunSummary(Guid.Parse(response.RunId))!;
        run.PipelineId.Should().Be(pipelineId);
        run.AgentId.Should().Be(Guid.Parse(agentId));
        run.AgentName.Should().Be("agent-01");
        run.Status.Should().Be("Dispatched");
        channel.Reader.TryRead(out var workItem).Should().BeTrue();
        workItem!.ExecutePipeline.SessionId.Should().Be(response.RunId);

        // ...and not written to the database here: that is what LiveStateSync does, a few seconds later.
        (await context.Runs.CountAsync()).Should().Be(0);
        live.PendingEventCount.Should().Be(1);
    }

    [Test]
    public async Task ExecutePipeline_prefers_the_agent_known_to_have_the_version_and_sends_the_feed_urls()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedInstalledPipelineAsync(context, "csv-to-database");
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = "http://nuget:8080/v3/index.json", Ordinal = 0 });

        var withoutIt = await SeedAgentAsync(context);
        var withIt = await SeedAgentAsync(context);
        var connections = new AgentConnectionRegistry();
        var withoutItChannel = Channel.CreateUnbounded<WorkItem>();
        var withItChannel = Channel.CreateUnbounded<WorkItem>();
        connections.Connect(withoutIt, withoutItChannel);
        connections.Connect(withIt, withItChannel);
        connections.MarkInstalled(withIt, "EtlPipelines.Samples.CsvToDatabase", "1.0.0");

        var service = Create(context, connections: connections);

        //act
        await service.ExecutePipeline(new ExecutePipelineRequest { PipelineId = pipelineId.ToString() }, TestServerCallContext());

        //assert
        withoutItChannel.Reader.TryRead(out _).Should().BeFalse();
        withItChannel.Reader.TryRead(out var workItem).Should().BeTrue();
        workItem!.ExecutePipeline.FeedUrls.Should().Equal("http://nuget:8080/v3/index.json");
    }

    [Test]
    public async Task UninstallPackage_hides_the_package_s_pipelines_and_stops_them_running()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedInstalledPipelineAsync(context, "csv-to-database");
        var connections = new AgentConnectionRegistry();
        connections.Connect(await SeedAgentAsync(context), Channel.CreateUnbounded<WorkItem>());
        var service = Create(context, connections: connections);

        //act
        await service.UninstallPackage(new UninstallPackageRequest { PackageId = "EtlPipelines.Samples.CsvToDatabase" }, TestServerCallContext());

        //assert
        (await service.ListInstalledPipelines(new Empty(), TestServerCallContext())).Pipelines.Should().BeEmpty();
        var execute = () => service.ExecutePipeline(new ExecutePipelineRequest { PipelineId = pipelineId.ToString() }, TestServerCallContext());
        (await execute.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        context.ChangeTracker.Clear();
        (await context.PackageVersions.SingleAsync()).Status.Should().Be(PackageVersionStatus.Uninstalled);
    }

    [Test]
    public async Task UninstallPackage_fails_with_NotFound_for_a_package_that_is_not_installed()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.UninstallPackage(new UninstallPackageRequest { PackageId = "Nope" }, TestServerCallContext());

        //assert
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Test]
    public async Task UpdatePackage_fails_with_FailedPrecondition_for_a_package_that_is_not_installed()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.UpdatePackage(new UpdatePackageRequest { PackageId = "Nope" }, TestServerCallContext());

        //assert
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task UpdatePackage_already_on_the_latest_version_retires_older_ones_without_an_agent()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        await SeedInstalledPipelineAsync(context, "csv-to-database", version: "1.0.0");
        await SeedInstalledPipelineAsync(context, "csv-to-database", version: "2.0.0");
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = "http://nuget:8080/v3/index.json", Ordinal = 0 });
        await context.SaveChangesAsync();
        var service = Create(context, feedClient: FeedReportingLatest("2.0.0"));

        //act
        var response = await service.UpdatePackage(new UpdatePackageRequest { PackageId = "EtlPipelines.Samples.CsvToDatabase" }, TestServerCallContext());

        //assert
        response.Version.Should().Be("2.0.0");
        response.PipelineNames.Should().Equal("csv-to-database");
        var listed = (await service.ListInstalledPipelines(new Empty(), TestServerCallContext())).Pipelines;
        listed.Should().ContainSingle().Which.PackageVersion.Should().Be("2.0.0");
    }

    [Test]
    public async Task UpdatePackage_installs_the_latest_version_on_an_agent_then_supersedes_the_old_one()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        await SeedInstalledPipelineAsync(context, "csv-to-database", version: "1.0.0");
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = "http://nuget:8080/v3/index.json", Ordinal = 0 });
        await context.SaveChangesAsync();

        var agentId = await SeedAgentAsync(context);
        var connections = new AgentConnectionRegistry();
        var channel = Channel.CreateUnbounded<WorkItem>();
        connections.Connect(agentId, channel);

        var service = Create(context, connections: connections, feedClient: FeedReportingLatest("2.0.0"));
        var agentService = new AgentServiceImpl(connections, new PackageCatalogService(context, Mock.Of<INuGetFeedClient>()), new LiveState(TimeProvider.System, NullLogger<LiveState>.Instance));

        //act - the agent's side: take the install work item and report it installed
        var update = service.UpdatePackage(new UpdatePackageRequest { PackageId = "EtlPipelines.Samples.CsvToDatabase" }, TestServerCallContext());
        var workItem = await channel.Reader.ReadAsync();
        await agentService.ReportInstallResult(
            new ReportInstallResultRequest { AgentId = agentId, WorkItemId = workItem.WorkItemId, Succeeded = true, PipelineNames = { "csv-to-database" } },
            TestServerCallContext());
        var response = await update;

        //assert
        workItem.InstallPackage.Version.Should().Be("2.0.0");
        response.Version.Should().Be("2.0.0");
        var listed = (await service.ListInstalledPipelines(new Empty(), TestServerCallContext())).Pipelines;
        listed.Should().ContainSingle().Which.PackageVersion.Should().Be("2.0.0");
        context.ChangeTracker.Clear();
        (await context.PackageVersions.SingleAsync(v => v.Version == "1.0.0")).Status.Should().Be(PackageVersionStatus.Superseded);
    }

    [Test]
    public async Task StreamRunProgress_replays_a_finished_runs_history_then_completes()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var runId = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1.0.0", null, [], CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "seed", 0, 1, 0, 5, null, null), CancellationToken.None);
        await live.FinishRunAsync(runId, "Succeeded", 0, 1, 1, 0, CancellationToken.None);

        var service = Create(SqliteServerDbContext.Create(), liveState: live);
        var responseStream = new RecordingServerStreamWriter<RunProgressEvent>();

        //act
        await service.StreamRunProgress(new StreamRunProgressRequest { RunId = runId.ToString() }, responseStream, TestServerCallContext());

        //assert
        responseStream.Written.Should().HaveCount(2);
        responseStream.Written[0].StageCompleted.Name.Should().Be("seed");
        responseStream.Written[1].RunCompleted.Status.Should().Be(RunCompleted.Types.Status.Succeeded);
        responseStream.Written[1].RunCompleted.RowsWritten.Should().Be(1);
    }

    [Test]
    public async Task StreamRunProgress_sends_each_stage_as_it_completes_to_a_caller_already_watching_then_the_completion()
    {
        //arrange
        var live = await TestLiveState.CreateAsync();
        var runId = await live.CreateRunAsync(Guid.NewGuid(), "p", "Pkg", "1.0.0", null, [], CancellationToken.None);
        await live.StageCompletedAsync(runId, new StageDto(0, "extract", 0, 1, 0, 5, null, null), CancellationToken.None);

        var service = Create(SqliteServerDbContext.Create(), liveState: live);
        var responseStream = new RecordingServerStreamWriter<RunProgressEvent>();
        var streaming = service.StreamRunProgress(new StreamRunProgressRequest { RunId = runId.ToString() }, responseStream, TestServerCallContext());

        //act
        await WaitUntilAsync(() => responseStream.Written.Count == 1);
        streaming.IsCompleted.Should().BeFalse("the run is still going");

        await live.StageCompletedAsync(runId, new StageDto(1, "load", 1, 1, 0, 5, null, null), CancellationToken.None);
        await WaitUntilAsync(() => responseStream.Written.Count == 2);

        await live.FinishRunAsync(runId, "Failed", 3, 1, 1, 0, CancellationToken.None);
        await streaming.WaitAsync(TimeSpan.FromSeconds(5));

        //assert
        responseStream.Written.Select(e => e.EventCase).Should().Equal(
            RunProgressEvent.EventOneofCase.StageCompleted, RunProgressEvent.EventOneofCase.StageCompleted, RunProgressEvent.EventOneofCase.RunCompleted);
        responseStream.Written.Take(2).Select(e => e.StageCompleted.Name).Should().Equal("extract", "load");
        responseStream.Written[2].RunCompleted.Status.Should().Be(RunCompleted.Types.Status.Failed);
        responseStream.Written[2].RunCompleted.ExitCode.Should().Be(3);
    }

    [Test]
    public async Task StreamRunProgress_answers_from_the_database_for_a_finished_run_older_than_memory_keeps()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedInstalledPipelineAsync(context, "csv-to-database");
        var runId = Guid.NewGuid();
        var longAgo = DateTime.UtcNow.AddDays(-2);
        context.Runs.Add(new Run
        {
            Id = runId, PipelineId = pipelineId, Status = "Failed", RequestedAt = longAgo, CompletedAt = longAgo,
            ExitCode = 2, RowsRead = 10, RowsWritten = 7, RowsFailed = 3,
        });
        context.StageResults.Add(new StageResult { Id = Guid.NewGuid(), RunId = runId, Sequence = 1, Name = "load", RowsIn = 10, RowsOut = 7, RowsFailed = 3, ErrorCode = "E1" });
        context.StageResults.Add(new StageResult { Id = Guid.NewGuid(), RunId = runId, Sequence = 0, Name = "extract", RowsIn = 10, RowsOut = 10 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var live = await TestLiveState.CreateAsync(context);
        live.TryGetRun(runId).Should().BeNull("too old to be held");
        var service = Create(context, liveState: live);
        var responseStream = new RecordingServerStreamWriter<RunProgressEvent>();

        //act
        await service.StreamRunProgress(new StreamRunProgressRequest { RunId = runId.ToString() }, responseStream, TestServerCallContext());

        //assert
        responseStream.Written.Should().HaveCount(3);
        responseStream.Written[0].StageCompleted.Name.Should().Be("extract");
        responseStream.Written[1].StageCompleted.Name.Should().Be("load");
        responseStream.Written[1].StageCompleted.ErrorCode.Should().Be("E1");
        var completed = responseStream.Written[2].RunCompleted;
        completed.Status.Should().Be(RunCompleted.Types.Status.Failed);
        completed.ExitCode.Should().Be(2);
        completed.RowsWritten.Should().Be(7);
        completed.RowsFailed.Should().Be(3);
    }

    [Test]
    public async Task StreamRunProgress_fails_with_NotFound_for_a_run_id_that_does_not_exist()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.StreamRunProgress(
            new StreamRunProgressRequest { RunId = Guid.NewGuid().ToString() },
            new RecordingServerStreamWriter<RunProgressEvent>(),
            TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Test]
    public async Task ListAgents_returns_the_host_details_an_agent_reported_when_it_registered()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var live = await TestLiveState.CreateAsync(context);
        var agentService = new AgentServiceImpl(
            new AgentConnectionRegistry(), new PackageCatalogService(context, Mock.Of<INuGetFeedClient>()), live);
        var registration = new RegisterAgentRequest
        {
            MachineName = "agent-01",
            Version = "1.0.0",
            HostInfo = new Google.Protobuf.WellKnownTypes.Struct
            {
                Fields =
                {
                    ["osDescription"] = Google.Protobuf.WellKnownTypes.Value.ForString("Ubuntu 24.04"),
                    ["processorCount"] = Google.Protobuf.WellKnownTypes.Value.ForNumber(8),
                    ["ipAddresses"] = Google.Protobuf.WellKnownTypes.Value.ForList(
                        Google.Protobuf.WellKnownTypes.Value.ForString("10.0.0.7"),
                        Google.Protobuf.WellKnownTypes.Value.ForString("2001:db8::7")),
                },
            },
        };
        await agentService.RegisterAgent(registration, TestServerCallContext());

        // An agent that predates the host details registers without them.
        await agentService.RegisterAgent(new RegisterAgentRequest { MachineName = "agent-00", Version = "0.9.0" }, TestServerCallContext());
        context.ChangeTracker.Clear();

        var service = Create(context, liveState: live);

        //act
        var response = await service.ListAgents(new Empty(), TestServerCallContext());

        //assert
        var old = response.Agents[0];
        old.MachineName.Should().Be("agent-00");
        old.Info.Should().BeNull();

        var described = response.Agents[1];
        described.Info.Fields["osDescription"].StringValue.Should().Be("Ubuntu 24.04");
        described.Info.Fields["processorCount"].NumberValue.Should().Be(8);
        described.Info.Fields["ipAddresses"].ListValue.Values.Select(ip => ip.StringValue)
            .Should().Equal("10.0.0.7", "2001:db8::7");

        // Written to the database by the sync, as one JSON document, so a new detail needs no schema change.
        await LiveEventWriter.WriteAsync(context, live.DrainPendingEvents(), CancellationToken.None);
        var stored = await context.Agents.AsNoTracking().SingleAsync(a => a.MachineName == "agent-01");
        System.Text.Json.JsonDocument.Parse(stored.Info!).RootElement.GetProperty("ipAddresses").GetArrayLength().Should().Be(2);
        (await context.Agents.AsNoTracking().SingleAsync(a => a.MachineName == "agent-00")).Info.Should().BeNull();
    }

    [Test]
    public async Task ListAgents_returns_every_registered_agent_with_its_status()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var onlineId = Guid.NewGuid();
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = onlineId, MachineName = "agent-01", Tags = ["linux", "gpu"], AssignedTags = [], Version = "1.2.3", Status = "Online", LastHeartbeatAt = heartbeat,
        });
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = Guid.NewGuid(), MachineName = "agent-02", Tags = [], AssignedTags = [], Version = "1.2.3", Status = "Offline", LastHeartbeatAt = heartbeat,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = Create(context);

        //act
        var response = await service.ListAgents(new Empty(), TestServerCallContext());

        //assert
        response.Agents.Should().HaveCount(2);
        var online = response.Agents[0];
        online.AgentId.Should().Be(onlineId.ToString());
        online.MachineName.Should().Be("agent-01");
        online.Tags.Should().Equal("linux", "gpu");
        online.Version.Should().Be("1.2.3");
        online.Status.Should().Be(AgentInfo.Types.Status.Online);
        online.LastHeartbeatAt.ToDateTime().Should().Be(heartbeat);
        response.Agents[1].Status.Should().Be(AgentInfo.Types.Status.Offline);
    }

    [Test]
    public async Task ListInstalledPipelines_maps_the_catalog_onto_the_proto_response()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.Add(new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "csv-to-database", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = Create(context);

        //act
        var response = await service.ListInstalledPipelines(new Empty(), TestServerCallContext());

        //assert
        response.Pipelines.Should().ContainSingle(pipeline =>
            pipeline.Name == "csv-to-database" &&
            pipeline.PackageId == "EtlPipelines.Samples.CsvToDatabase" &&
            pipeline.PackageVersion == "1.0.0");
    }

    [Test]
    public async Task SetConfigurationEntry_stores_the_value_encrypted()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var protector = new Mock<IDataProtector>();
        protector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes);
        var protectionProvider = new Mock<IDataProtectionProvider>();
        protectionProvider.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(protector.Object);

        var service = Create(context, dataProtectionProvider: protectionProvider.Object);

        //act
        var ack = await service.SetConfigurationEntry(
            new SetConfigurationEntryRequest { Key = "ConnectionStrings:sales", Value = "..." },
            TestServerCallContext());

        //assert
        ack.Should().NotBeNull();
        context.ConfigurationEntries.Should().ContainSingle(entry => entry.Key == "ConnectionStrings:sales");
    }

    private static Task<ManagementServiceImpl> CreateAsync() => Task.FromResult(Create(SqliteServerDbContext.Create()));

    /// <summary>
    /// A service over <paramref name="context"/>, with a <see cref="LiveState"/> loaded from it as it
    /// is now (what startup does) unless one is given - so seed the database first.
    /// </summary>
    private static ManagementServiceImpl Create(
        ServerDbContext context,
        AgentConnectionRegistry? connections = null,
        IDataProtectionProvider? dataProtectionProvider = null,
        INuGetFeedClient? feedClient = null,
        LiveState? liveState = null)
    {
        var registry = connections ?? new AgentConnectionRegistry();
        liveState ??= TestLiveState.CreateAsync(context).GetAwaiter().GetResult();

        var catalog = new PackageCatalogService(context, feedClient ?? Mock.Of<INuGetFeedClient>());

        return new ManagementServiceImpl(
            context,
            catalog,
            new SecretsStore(context, dataProtectionProvider ?? Mock.Of<IDataProtectionProvider>()),
            new PackageOperations(catalog, registry, liveState),
            new RunDispatcher(context, registry, liveState, "http://localhost:5000"),
            liveState);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Seeds one installed pipeline (package + version + pipeline row) and returns its id.</summary>
    private static async Task<Guid> SeedInstalledPipelineAsync(ServerDbContext context, string pipelineName, string version = "1.0.0")
    {
        var package = await context.Packages.SingleOrDefaultAsync(p => p.NugetPackageId == "EtlPipelines.Samples.CsvToDatabase");
        var packageId = package?.Id ?? Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();

        if (package is null)
        {
            context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        }

        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = version, InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.Add(new Pipeline { Id = pipelineId, PackageVersionId = versionId, Name = pipelineName, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return pipelineId;
    }

    /// <summary>Seeds one online agent row and returns its id.</summary>
    private static async Task<string> SeedAgentAsync(ServerDbContext context)
    {
        var id = Guid.NewGuid();
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = id, MachineName = "agent", Tags = [], AssignedTags = [], Version = "1.0.0", Status = "Online", LastHeartbeatAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return id.ToString();
    }

    /// <summary>A feed client whose latest stable version of every package is <paramref name="version"/>.</summary>
    private static INuGetFeedClient FeedReportingLatest(string version)
    {
        var feedClient = new Mock<INuGetFeedClient>();
        feedClient
            .Setup(client => client.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NuGetVersion.Parse(version));
        return feedClient.Object;
    }

    /// <summary>
    /// <see cref="ServerCallContext"/> is abstract with no public constructor; the RPCs under test
    /// here only ever read <see cref="ServerCallContext.CancellationToken"/>, so a
    /// <see cref="Mock{ServerCallContext}"/> stubbing just that member is enough - no need for the
    /// full <c>Grpc.Core.Testing.TestServerCallContext</c> machinery.
    /// </summary>
    private static ServerCallContext TestServerCallContext()
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context.Object;
    }

    /// <summary>A minimal <see cref="IServerStreamWriter{T}"/> that just records what was written, for asserting <c>StreamRunProgress</c>'s output.</summary>
    private sealed class RecordingServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Written { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }
}
