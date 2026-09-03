using System.Net;
using System.Net.Sockets;
using CliWrap;
using CliWrap.Buffered;
using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Database;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EtlPipelines.Server.Tests.EndToEnd;

/// <summary>
/// The full happy path SERVER.md's Phase 6 exists for: <c>ExecutePipeline</c> dispatches to a
/// connected agent, which launches an installed pipeline package's shim with a session id and a
/// server url; the launched process pulls its (empty) configuration, runs for real, and reports
/// its own progress and result back over <c>PipelineExecutionService</c> - proven by polling
/// <c>StreamRunProgress</c> and then checking <c>Runs</c>/<c>StageResults</c> directly.
/// </summary>
/// <remarks>
/// Builds on exactly what <see cref="InstallLoopDockerTests"/> already proves (install closes the
/// loop) - this test installs the same way, then goes one step further and actually executes what
/// it just installed. Every sample gets the session-id/server-url wiring for free (it is built
/// into <c>EtlPipelinesHost</c> itself, not an opt-in any one sample asks for), so
/// <c>EtlPipelines.Samples.CsvToDatabase</c> is this phase's own pick for a different reason
/// (SERVER.md: "already proven against five real database engines") - the run this test triggers
/// is the first one this repo runs through the real gRPC round trip end to end rather than only
/// through <c>tests/EtlPipelines.Samples.Tests</c>' in-process/CLI coverage.
/// </remarks>
[Category("Docker")]
[ClassDataSource<PostgreSqlFixture, BagetterFixture>(Shared = [SharedType.PerAssembly, SharedType.PerAssembly])]
public class ExecutePipelineDockerTests(PostgreSqlFixture postgres, BagetterFixture bagetter)
{
    private const string PackageId = "EtlPipelines.Samples.CsvToDatabase";
    private const string PipelineName = "trades";

    // Same NuGetFeeds-sharing reasoning as InstallLoopDockerTests' own copy of this attribute -
    // one Postgres container, SharedType.PerAssembly, across every Docker-tagged test in this
    // project.
    [Test]
    [NotInParallel("NuGetFeeds")]
    public async Task ExecutePipeline_runs_the_installed_sample_end_to_end_and_settles_Runs_and_StageResults()
    {
        //arrange
        await SchemaDeployer.EnsureDeployedAsync(postgres.ConnectionString);
        await ClearNuGetFeedsAsync();
        var version = await PackAndPushAsync();

        // Reserved ahead of Kestrel binding it for real, so this test can pass a real,
        // already-known address as Server:PublicUrl before ServerApplication.Build() ever runs -
        // the launched pipeline process's own --server-url has to be correct from the start, and
        // "127.0.0.1" here (rather than leaving Server:PublicUrl to its own "localhost" fallback)
        // matches exactly what GetBoundUrl below already proves reachable for the test's own
        // client, rather than trusting a second, never-otherwise-exercised hostname to resolve
        // the same way on whatever machine runs this.
        var port = GetFreeTcpPort();
        var publicUrl = $"http://127.0.0.1:{port}";

        await using var server = EtlPipelines.Server.ServerApplication.Build([
            "--Server:Port", port.ToString(),
            "--Server:PublicUrl", publicUrl,
            "--ConnectionStrings:Server", postgres.ConnectionString,
            "--NuGetFeed:Url", bagetter.FeedUrl,
        ]);
        await server.StartAsync();
        var serverUrl = GetBoundUrl(server);

        var agentCacheDirectory = Path.Combine(Path.GetTempPath(), $"etlpipelines-agent-cache-{Guid.NewGuid():N}");
        using var agentHost = EtlPipelines.Agent.AgentApplication.Build([
            "--Agent:ServerUrl", serverUrl,
            "--Agent:CacheDirectory", agentCacheDirectory,
        ]);
        await agentHost.StartAsync();

        try
        {
            await WaitForAgentToConnectAsync(server, TimeSpan.FromSeconds(20));

            using var channel = CreateChannel(serverUrl);
            var client = new ManagementService.ManagementServiceClient(channel);

            var installResponse = await client.InstallPackageAsync(new InstallPackageRequest { PackageId = PackageId, Version = version });
            installResponse.PipelineNames.Should().Contain(PipelineName);

            var installed = await client.ListInstalledPipelinesAsync(new Empty());
            var pipelineId = installed.Pipelines.Single(p => p.PackageId == PackageId && p.Name == PipelineName).PipelineId;

            //act
            var executeResponse = await client.ExecutePipelineAsync(new ExecutePipelineRequest { PipelineId = pipelineId });
            executeResponse.RunId.Should().NotBeNullOrEmpty();

            // Generous, matching ManagementServiceImpl.InstallTimeout's own reasoning: this run
            // starts with the agent's own real dotnet tool install replay-through (the shim was
            // already installed above, but the process this launches still has to JIT-start a
            // real framework-dependent .NET app) before it ever touches a single CSV row, and a
            // loaded CI runner is not this repo's own machine.
            var progressEvents = await StreamToCompletionAsync(client, executeResponse.RunId, TimeSpan.FromMinutes(5));

            //assert
            progressEvents.Should().NotBeEmpty("at least the run's own completion event should have arrived");
            progressEvents.Should().Contain(e => e.EventCase == RunProgressEvent.EventOneofCase.StageCompleted, "the pipeline has at least one dataflow stage");

            var runCompleted = progressEvents.Should().ContainSingle(e => e.EventCase == RunProgressEvent.EventOneofCase.RunCompleted)
                .Which.RunCompleted;
            runCompleted.Status.Should().Be(RunCompleted.Types.Status.Succeeded);
            runCompleted.RowsWritten.Should().Be(EtlPipelines.Samples.CsvToDatabase.Pipeline.ExpectedRows);

            var runId = Guid.Parse(executeResponse.RunId);
            var options = new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(postgres.ConnectionString).Options;
            await using var dbContext = new ServerDbContext(options);

            var run = await dbContext.Runs.SingleAsync(r => r.Id == runId);
            run.Status.Should().Be("Succeeded");
            run.ExitCode.Should().Be(0);
            run.RowsWritten.Should().Be(EtlPipelines.Samples.CsvToDatabase.Pipeline.ExpectedRows);

            var stageResults = await dbContext.StageResults.Where(s => s.RunId == runId).ToListAsync();
            stageResults.Should().NotBeEmpty();
        }
        finally
        {
            await agentHost.StopAsync();
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
        var work = Directory.CreateTempSubdirectory("EtlPipelines.Server.Tests.ExecutePipeline.");

        try
        {
            var projectPath = RepositoryPaths.SourceProjectFile(PackageId);

            var pack = await Cli.Wrap("dotnet")
                .WithArguments(["pack", projectPath, "--configuration", "Release", "--output", work.FullName])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            pack.ExitCode.Should().Be(0, $"dotnet pack should succeed: {Tail(pack)}");

            var nupkg = Directory.GetFiles(work.FullName, "*.nupkg").Single();

            var push = await Cli.Wrap("dotnet")
                .WithArguments(["nuget", "push", nupkg, "--source", bagetter.FeedUrl, "--api-key", "any", "--allow-insecure-connections"])
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

    /// <summary>Consumes <c>StreamRunProgress</c> until the server closes it (the run completed), or <paramref name="timeout"/> elapses.</summary>
    private static async Task<List<RunProgressEvent>> StreamToCompletionAsync(
        ManagementService.ManagementServiceClient client, string runId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var call = client.StreamRunProgress(new StreamRunProgressRequest { RunId = runId }, cancellationToken: cts.Token);

        var events = new List<RunProgressEvent>();
        await foreach (var progressEvent in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            events.Add(progressEvent);
        }

        return events;
    }

    /// <summary>A free TCP port, reserved just long enough to read it back - see this test's own remarks on why it needs one ahead of time.</summary>
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
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
