using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Secrets;
using EtlPipelines.Server.Services;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.Protected;

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
    public async Task ExecutePipeline_dispatches_to_a_connected_agent_and_creates_a_Run()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedInstalledPipelineAsync(context, "csv-to-database");

        var agentGuid = Guid.NewGuid();
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = agentGuid, MachineName = "agent-01", Tags = [], Version = "1.0.0", Status = "Online", LastHeartbeatAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var connections = new AgentConnectionRegistry();
        var agentId = agentGuid.ToString();
        connections.Connect(agentId, Channel.CreateUnbounded<WorkItem>());

        var service = Create(context, connections: connections);

        //act
        var response = await service.ExecutePipeline(new ExecutePipelineRequest { PipelineId = pipelineId.ToString() }, TestServerCallContext());

        //assert
        response.RunId.Should().NotBeNullOrEmpty();
        var run = await context.Runs.SingleAsync(r => r.Id == Guid.Parse(response.RunId));
        run.PipelineId.Should().Be(pipelineId);
        run.AgentId.Should().Be(Guid.Parse(agentId));
        run.Status.Should().Be("Dispatched");
    }

    [Test]
    public async Task StreamRunProgress_replays_history_then_completes_after_RunCompleted()
    {
        //arrange
        var statusStore = new RunStatusStore();
        var runId = Guid.NewGuid();
        statusStore.PublishStageCompleted(runId, new StageCompleted { Sequence = 0, Name = "seed" });
        statusStore.PublishRunCompleted(runId, new RunCompleted { Status = RunCompleted.Types.Status.Succeeded });

        var service = Create(SqliteServerDbContext.Create(), statusStore: statusStore);
        var responseStream = new RecordingServerStreamWriter<RunProgressEvent>();

        //act
        await service.StreamRunProgress(new StreamRunProgressRequest { RunId = runId.ToString() }, responseStream, TestServerCallContext());

        //assert
        responseStream.Written.Should().HaveCount(2);
        responseStream.Written[0].StageCompleted.Should().NotBeNull();
        responseStream.Written[1].RunCompleted.Status.Should().Be(RunCompleted.Types.Status.Succeeded);
    }

    [Test]
    public async Task StreamRunProgress_answers_from_the_database_for_a_finished_run_the_store_no_longer_tracks()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var pipelineId = await SeedInstalledPipelineAsync(context, "csv-to-database");
        var runId = Guid.NewGuid();
        context.Runs.Add(new Run
        {
            Id = runId, PipelineId = pipelineId, Status = "Failed", RequestedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
            ExitCode = 2, RowsRead = 10, RowsWritten = 7, RowsFailed = 3,
        });
        context.StageResults.Add(new StageResult { Id = Guid.NewGuid(), RunId = runId, Sequence = 1, Name = "load", RowsIn = 10, RowsOut = 7, RowsFailed = 3, ErrorCode = "E1" });
        context.StageResults.Add(new StageResult { Id = Guid.NewGuid(), RunId = runId, Sequence = 0, Name = "extract", RowsIn = 10, RowsOut = 10 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = Create(context);
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
    public async Task ListAgents_returns_every_registered_agent_with_its_status()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var onlineId = Guid.NewGuid();
        var heartbeat = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = onlineId, MachineName = "agent-01", Tags = ["linux", "gpu"], Version = "1.2.3", Status = "Online", LastHeartbeatAt = heartbeat,
        });
        context.Agents.Add(new Database.Entities.Agent
        {
            Id = Guid.NewGuid(), MachineName = "agent-02", Tags = [], Version = "1.2.3", Status = "Offline", LastHeartbeatAt = heartbeat,
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

    private static ManagementServiceImpl Create(
        ServerDbContext context,
        AgentConnectionRegistry? connections = null,
        IDataProtectionProvider? dataProtectionProvider = null,
        RunStatusStore? statusStore = null)
    {
        var registry = connections ?? new AgentConnectionRegistry();

        return new ManagementServiceImpl(
            context,
            new PackageCatalogService(context, Mock.Of<INuGetFeedClient>()),
            new SecretsStore(context, dataProtectionProvider ?? Mock.Of<IDataProtectionProvider>()),
            registry,
            new RunDispatcher(context, registry, "http://localhost:5000"),
            statusStore ?? new RunStatusStore());
    }

    /// <summary>Seeds one installed pipeline (package + version + pipeline row) and returns its id.</summary>
    private static async Task<Guid> SeedInstalledPipelineAsync(ServerDbContext context, string pipelineName)
    {
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.Add(new Pipeline { Id = pipelineId, PackageVersionId = versionId, Name = pipelineName, CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return pipelineId;
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
