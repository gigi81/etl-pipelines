using EtlPipelines.Management.V1;
using EtlPipelines.PipelineExecution.V1;
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
/// Proves <see cref="PipelineExecutionServiceImpl"/> - the narrowest, least-trusted of the three
/// v1 services (SERVER.md Phase 6) - resolves <c>session_id</c> against a real <c>Runs</c> row,
/// persists what it is told, and publishes to <see cref="RunStatusStore"/> along the way.
/// </summary>
[Category("Server")]
public class PipelineExecutionServiceImplTests
{
    [Test]
    public async Task GetConfiguration_fails_with_NotFound_for_an_unknown_session()
    {
        //arrange
        var service = Create(SqliteServerDbContext.Create());

        //act
        var act = () => service.GetConfiguration(new GetConfigurationRequest { SessionId = Guid.NewGuid().ToString() }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Test]
    public async Task GetConfiguration_fails_with_InvalidArgument_for_a_malformed_session_id()
    {
        //arrange
        var service = Create(SqliteServerDbContext.Create());

        //act
        var act = () => service.GetConfiguration(new GetConfigurationRequest { SessionId = "not-a-guid" }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task GetConfiguration_returns_every_entry_decrypted_and_flips_Dispatched_to_Running()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var runId = await SeedRunAsync(context, "Dispatched");

        var protector = new Mock<IDataProtector>();
        protector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes);
        protector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes);
        var protectionProvider = new Mock<IDataProtectionProvider>();
        protectionProvider.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(protector.Object);

        var secretsStore = new SecretsStore(context, protectionProvider.Object);
        await secretsStore.SetAsync("ConnectionStrings:sales", "Host=db;", CancellationToken.None);

        var service = Create(context, secretsStore);

        //act
        var response = await service.GetConfiguration(new GetConfigurationRequest { SessionId = runId.ToString() }, TestServerCallContext());

        //assert
        response.Entries["ConnectionStrings:sales"].Should().Be("Host=db;");

        var run = await context.Runs.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be("Running");
        run.StartedAt.Should().NotBeNull();
    }

    [Test]
    public async Task ReportStageResult_persists_a_StageResult_row_and_publishes_it()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var runId = await SeedRunAsync(context, "Running");
        var statusStore = new RunStatusStore();
        var service = Create(context, statusStore: statusStore);
        var reader = statusStore.Subscribe(runId);

        //act
        await service.ReportStageResult(new ReportStageResultRequest
        {
            SessionId = runId.ToString(),
            Sequence = 0,
            Name = "seed",
            RowsIn = 10,
            RowsOut = 9,
            RowsFailed = 1,
            ElapsedMs = 42,
        }, TestServerCallContext());

        //assert
        var stored = await context.StageResults.SingleAsync(s => s.RunId == runId);
        stored.Name.Should().Be("seed");
        stored.RowsIn.Should().Be(10);
        stored.RowsOut.Should().Be(9);
        stored.RowsFailed.Should().Be(1);
        stored.ErrorCode.Should().BeNull();

        (await reader.ReadAsync()).StageCompleted.Name.Should().Be("seed");
    }

    [Test]
    public async Task ReportRunResult_settles_the_Run_row_and_publishes_completion()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var runId = await SeedRunAsync(context, "Running");
        var statusStore = new RunStatusStore();
        var service = Create(context, statusStore: statusStore);
        var reader = statusStore.Subscribe(runId);

        //act
        await service.ReportRunResult(new ReportRunResultRequest
        {
            SessionId = runId.ToString(),
            Outcome = ReportRunResultRequest.Types.Outcome.Succeeded,
            ExitCode = 0,
            RowsRead = 10,
            RowsWritten = 9,
            RowsFailed = 1,
        }, TestServerCallContext());

        //assert
        var run = await context.Runs.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be("Succeeded");
        run.ExitCode.Should().Be(0);
        run.RowsRead.Should().Be(10);
        run.CompletedAt.Should().NotBeNull();

        var progressEvent = await reader.ReadAsync();
        progressEvent.RunCompleted.Status.Should().Be(RunCompleted.Types.Status.Succeeded);
        (await reader.WaitToReadAsync()).Should().BeFalse("the stream completes once the run itself completes");
    }

    [Test]
    public async Task Heartbeat_fails_with_NotFound_for_an_unknown_session()
    {
        //arrange
        var service = Create(SqliteServerDbContext.Create());

        //act
        var act = () => service.Heartbeat(new HeartbeatRequest { SessionId = Guid.NewGuid().ToString() }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    private static PipelineExecutionServiceImpl Create(
        ServerDbContext context, SecretsStore? secretsStore = null, RunStatusStore? statusStore = null) =>
        new(
            context,
            // A loose mock returning null from CreateProtector is fine for every test here that
            // uses this default - none of them ever populate ConfigurationEntries, so
            // SecretsStore.GetAllAsync never actually touches the protector.
            secretsStore ?? new SecretsStore(context, Mock.Of<IDataProtectionProvider>()),
            statusStore ?? new RunStatusStore());

    /// <summary>Seeds a full package/version/pipeline/run chain and returns the run's id (the session id).</summary>
    private static async Task<Guid> SeedRunAsync(ServerDbContext context, string status)
    {
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.Add(new Pipeline { Id = pipelineId, PackageVersionId = versionId, Name = "csv-to-database", CreatedAt = DateTime.UtcNow });
        context.Runs.Add(new Run { Id = runId, PipelineId = pipelineId, Status = status, RequestedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        return runId;
    }

    private static ServerCallContext TestServerCallContext()
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context.Object;
    }
}
