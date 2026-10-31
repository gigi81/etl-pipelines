using EtlPipelines.PipelineExecution.V1;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Secrets;
using EtlPipelines.Server.Services;
using EtlPipelines.Server.Tests.Live;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using Microsoft.EntityFrameworkCore;
using Moq.Protected;

namespace EtlPipelines.Server.Tests.Services;

/// <summary>
/// Proves <see cref="PipelineExecutionServiceImpl"/> - the narrowest, least-trusted of the three
/// v1 services - resolves <c>session_id</c> against the runs <see cref="LiveState"/> holds, and
/// records what it is told there (not in the database, which <see cref="LiveStateSync"/> writes behind).
/// </summary>
[Category("Server")]
public class PipelineExecutionServiceImplTests
{
    [Test]
    public async Task GetConfiguration_fails_with_NotFound_for_an_unknown_session()
    {
        //arrange
        var service = Create(SqliteServerDbContext.Create(), await TestLiveState.CreateAsync());

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
        var service = Create(SqliteServerDbContext.Create(), await TestLiveState.CreateAsync());

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
        var live = await TestLiveState.CreateAsync(context);

        var protector = new Mock<IDataProtector>();
        protector.Setup(p => p.Protect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes);
        protector.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns<byte[]>(bytes => bytes);
        var protectionProvider = new Mock<IDataProtectionProvider>();
        protectionProvider.Setup(p => p.CreateProtector(It.IsAny<string>())).Returns(protector.Object);

        var secretsStore = new SecretsStore(context, protectionProvider.Object);
        await secretsStore.SetAsync("ConnectionStrings:sales", "Host=db;", CancellationToken.None);

        var service = Create(context, live, secretsStore);

        //act
        var response = await service.GetConfiguration(new GetConfigurationRequest { SessionId = runId.ToString() }, TestServerCallContext());

        //assert
        response.Entries["ConnectionStrings:sales"].Should().Be("Host=db;");

        var run = live.TryGetRunSummary(runId)!;
        run.Status.Should().Be("Running");
        run.StartedAtUtc.Should().NotBeNull();
    }

    [Test]
    public async Task ReportStageResult_records_the_stage_and_notifies_listeners()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var runId = await SeedRunAsync(context, "Running");
        var live = await TestLiveState.CreateAsync(context);
        var service = Create(context, live);
        using var changes = live.Subscribe(change => change.Id == runId);

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
        var stage = live.TryGetRun(runId)!.Stages.Single();
        stage.Name.Should().Be("seed");
        (stage.RowsIn, stage.RowsOut, stage.RowsFailed, stage.ElapsedMs).Should().Be((10, 9, 1, 42));
        stage.ErrorCode.Should().BeNull();
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().NotBeEmpty();
        live.PendingEventCount.Should().Be(1, "queued for the database, which is not written to here");
        (await context.StageResults.CountAsync()).Should().Be(0);
    }

    [Test]
    public async Task ReportRunResult_settles_the_run()
    {
        //arrange
        var context = SqliteServerDbContext.Create();
        var runId = await SeedRunAsync(context, "Running");
        var live = await TestLiveState.CreateAsync(context);
        var service = Create(context, live);

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
        var run = live.TryGetRunSummary(runId)!;
        run.Status.Should().Be("Succeeded");
        run.ExitCode.Should().Be(0);
        (run.RowsRead, run.RowsWritten, run.RowsFailed).Should().Be((10, 9, 1));
        run.CompletedAtUtc.Should().NotBeNull();
    }

    [Test]
    public async Task Reports_for_an_unknown_run_fail_with_NotFound()
    {
        //arrange
        var service = Create(SqliteServerDbContext.Create(), await TestLiveState.CreateAsync());
        var unknown = Guid.NewGuid().ToString();

        //act + assert
        foreach (var act in new Func<Task>[]
        {
            () => service.ReportStageResult(new ReportStageResultRequest { SessionId = unknown }, TestServerCallContext()),
            () => service.ReportRunResult(new ReportRunResultRequest { SessionId = unknown }, TestServerCallContext()),
        })
        {
            var exception = await act.Should().ThrowAsync<RpcException>();
            exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
        }
    }

    [Test]
    public async Task Heartbeat_fails_with_NotFound_for_an_unknown_session()
    {
        //arrange
        var service = Create(SqliteServerDbContext.Create(), await TestLiveState.CreateAsync());

        //act
        var act = () => service.Heartbeat(new HeartbeatRequest { SessionId = Guid.NewGuid().ToString() }, TestServerCallContext());

        //assert
        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    private static PipelineExecutionServiceImpl Create(ServerDbContext context, LiveState liveState, SecretsStore? secretsStore = null) =>
        new(
            // A loose mock returning null from CreateProtector is fine for every test here that
            // uses this default - none of them ever populate ConfigurationEntries, so
            // SecretsStore.GetAllAsync never actually touches the protector.
            secretsStore ?? new SecretsStore(context, Mock.Of<IDataProtectionProvider>()),
            liveState);

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
