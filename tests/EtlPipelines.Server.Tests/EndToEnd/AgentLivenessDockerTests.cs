using CliWrap;
using CliWrap.Buffered;
using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Database;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EtlPipelines.Server.Tests.EndToEnd;

/// <summary>
/// The other half of SERVER.md Phase 8's own reliability work, proven end to end: an agent that
/// stops heartbeating mid-run (its process died, rather than a clean <c>Subscribe</c> disconnect)
/// is marked <c>Offline</c>, and the <c>Run</c> it was still carrying out is marked
/// <c>AgentLost</c>, within <see cref="AgentLivenessOptions.Timeout"/> - SERVER.md's own words:
/// "a [Category("Docker")] test that kills an agent container mid-run and asserts the server marks
/// the run AgentLost within the timeout window."
/// </summary>
/// <remarks>
/// Builds on exactly what <see cref="ExecutePipelineDockerTests"/> already proves (a dispatched run
/// executes for real, end to end) - this test dispatches the same way, then stops the agent host
/// instead of letting it finish. <see cref="IHostedService.StopAsync"/> is this test's stand-in for
/// SERVER.md's own "kills an agent container": every Docker-tagged test in this project already
/// runs <c>Server</c> and <c>Agent</c> in-process rather than as real separate containers (only
/// Postgres/bagetter get real Testcontainers - see <see cref="ExecutePipelineDockerTests"/>'s own
/// remarks), and stopping the agent host cancels <c>AgentRegistration</c>'s own <c>stoppingToken</c>
/// - tearing down the heartbeat loop, the <c>Subscribe</c> stream, and (via CliWrap's own
/// cancellation handling) whatever pipeline process was mid-launch - the same abrupt,
/// never-gets-to-report-anything-again shape a killed container would leave behind.
/// </remarks>
[Category("Docker")]
[ClassDataSource<PostgreSqlFixture, BagetterFixture>(Shared = [SharedType.PerAssembly, SharedType.PerAssembly])]
public class AgentLivenessDockerTests(PostgreSqlFixture postgres, BagetterFixture bagetter)
{
    private const string PackageId = "EtlPipelines.Samples.CsvToDatabase";
    private const string PipelineName = "trades";

    [Test]
    [NotInParallel("NuGetFeeds")]
    public async Task An_agent_that_stops_heartbeating_mid_run_is_marked_Offline_and_its_Run_AgentLost()
    {
        //arrange
        await SchemaDeployer.EnsureDeployedAsync(postgres.ConnectionString);
        await ClearNuGetFeedsAsync();
        var version = await PackAndPushAsync();

        await using var server = EtlPipelines.Server.ServerApplication.Build([
            "--Server:Port", "0",
            "--ConnectionStrings:Server", postgres.ConnectionString,
            "--NuGetFeed:Url", bagetter.FeedUrl,
            // Short enough that this test does not need to wait long for the sweep to notice;
            // generous enough that a loaded CI runner's own scheduling jitter never produces a
            // false negative the way AgentRegistration's real 10-second heartbeat interval would
            // if this were left at AgentLivenessOptions' own 30-second default.
            "--AgentLiveness:Timeout", "00:00:02",
            "--AgentLiveness:PollInterval", "00:00:01",
        ]);
        await server.StartAsync();
        var serverUrl = GetBoundUrl(server);

        var agentCacheDirectory = Path.Combine(Path.GetTempPath(), $"etlpipelines-agent-cache-{Guid.NewGuid():N}");
        using var agentHost = EtlPipelines.Agent.AgentApplication.Build([
            "--Agent:ServerUrl", serverUrl,
            "--Agent:CacheDirectory", agentCacheDirectory,
        ]);
        await agentHost.StartAsync();

        var agentStopped = false;
        try
        {
            await WaitForAgentToConnectAsync(server, TimeSpan.FromSeconds(20));

            using var channel = CreateChannel(serverUrl);
            var client = new ManagementService.ManagementServiceClient(channel);

            var installResponse = await client.InstallPackageAsync(new InstallPackageRequest { PackageId = PackageId, Version = version });
            installResponse.PipelineNames.Should().Contain(PipelineName);

            var installed = await client.ListInstalledPipelinesAsync(new Empty());
            var pipelineId = installed.Pipelines.Single(p => p.PackageId == PackageId && p.Name == PipelineName).PipelineId;

            //act - dispatch, then immediately stop the agent host, before the launched pipeline
            // process (if it even got that far) ever has a chance to report anything back - see
            // this class's own remarks for why StopAsync stands in for "the agent's container
            // gets killed" here.
            var executeResponse = await client.ExecutePipelineAsync(new ExecutePipelineRequest { PipelineId = pipelineId });
            await agentHost.StopAsync();
            agentStopped = true;

            var runId = Guid.Parse(executeResponse.RunId);

            //assert
            var options = new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(postgres.ConnectionString).Options;
            await using var dbContext = new ServerDbContext(options);

            var run = await PollUntilAsync(
                () => dbContext.Runs.AsNoTracking().SingleAsync(r => r.Id == runId),
                r => r.Status == "AgentLost",
                TimeSpan.FromSeconds(20));

            run.Status.Should().Be("AgentLost");
            run.CompletedAt.Should().NotBeNull();
            run.AgentId.Should().NotBeNull();

            var agent = await dbContext.Agents.AsNoTracking().SingleAsync(a => a.Id == run.AgentId);
            agent.Status.Should().Be("Offline");
        }
        finally
        {
            if (!agentStopped)
            {
                await agentHost.StopAsync();
            }

            try
            {
                Directory.Delete(agentCacheDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    private async Task ClearNuGetFeedsAsync()
    {
        var options = new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using var context = new ServerDbContext(options);
        context.NuGetFeeds.RemoveRange(await context.NuGetFeeds.ToListAsync());
        await context.SaveChangesAsync();
    }

    /// <summary>Packs the sample and pushes it to <see cref="bagetter"/>, returning the version <c>dotnet pack</c> gave it.</summary>
    private async Task<string> PackAndPushAsync()
    {
        var work = Directory.CreateTempSubdirectory("EtlPipelines.Server.Tests.AgentLiveness.");

        try
        {
            var projectPath = RepositoryPaths.SourceProjectFile(PackageId);

            var pack = await Cli.Wrap("dotnet")
                .WithArguments(["pack", projectPath, "--configuration", "Release", "--output", work.FullName])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            pack.ExitCode.Should().Be(0, $"dotnet pack should succeed: {Tail(pack)}");

            var nupkg = Directory.GetFiles(work.FullName, "*.nupkg").Single();

            // --skip-duplicate: AgentLivenessDockerTests and ExecutePipelineDockerTests both push this
            // same package and version to the one per-assembly bagetter container, so whichever runs
            // second would otherwise fail on a 409 Conflict for a package that is already there.
            var push = await Cli.Wrap("dotnet")
                .WithArguments(["nuget", "push", nupkg, "--source", bagetter.FeedUrl, "--api-key", "any", "--allow-insecure-connections", "--skip-duplicate"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            push.ExitCode.Should().Be(0, $"dotnet nuget push should succeed: {Tail(push)}");

            var fileName = Path.GetFileNameWithoutExtension(nupkg);
            return fileName[(PackageId.Length + 1)..];
        }
        finally
        {
            try
            {
                Directory.Delete(work.FullName, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    /// <summary>Polls <paramref name="query"/> until it returns a value <paramref name="isDone"/> accepts, or <paramref name="timeout"/> elapses.</summary>
    private static async Task<T> PollUntilAsync<T>(Func<Task<T>> query, Func<T, bool> isDone, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var value = await query().ConfigureAwait(false);
            if (isDone(value))
            {
                return value;
            }

            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Condition was not met within {timeout}. Last value: {value}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Same reasoning as <see cref="InstallLoopDockerTests"/>'s own copy of this helper.</summary>
    private static string GetBoundUrl(WebApplication app)
    {
        var boundAddress = app.Urls.FirstOrDefault() ?? throw new InvalidOperationException("Server did not report a bound URL.");
        return $"http://127.0.0.1:{new Uri(boundAddress).Port}";
    }

    private static GrpcChannel CreateChannel(string address)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return GrpcChannel.ForAddress(address);
    }

    private static async Task WaitForAgentToConnectAsync(WebApplication server, TimeSpan timeout)
    {
        var connections = server.Services.GetRequiredService<AgentConnectionRegistry>();
        using var cts = new CancellationTokenSource(timeout);

        while (!connections.TryGetAnyConnectedAgentId(out _))
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"No agent connected within {timeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }
    }

    private static string Tail(BufferedCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() : "(no output)";
    }
}
