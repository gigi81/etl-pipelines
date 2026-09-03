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
    public async Task ListAgents_is_still_the_Phase_2_stub()
    {
        //arrange
        var service = await CreateAsync();

        //act
        var act = () => service.ListAgents(new Empty(), TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.Unimplemented);
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
