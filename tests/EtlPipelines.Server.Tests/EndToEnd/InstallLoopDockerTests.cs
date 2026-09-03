using CliWrap;
using CliWrap.Buffered;
using EtlPipelines.AgentExecution.V1;
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
/// The one test in this project that runs a real <c>Server</c> and a real <c>Agent</c> together
/// against real infrastructure - Postgres, bagetter - and proves
/// <c>ManagementService.InstallPackage</c>'s dispatch-to-a-connected-agent round trip actually
/// closes: SERVER.md Phase 5's own "Tests" instruction ("a real bagetter container seeded with a
/// sample packed in Phase 1, real Server+Server.Database (Postgres container), real Agent -
/// assert ListInstalledPipelines reports the sample's pipeline names after InstallPackage").
/// Every other Docker-tagged test in this repo proves one side of this loop in isolation
/// (<c>ManagementServiceDockerTests</c> the server side against a mocked feed client,
/// <c>EtlPipelines.Agent.Tests.PackageInstallerTests</c> the agent side against a local package);
/// this is the one that proves the two sides actually agree over the wire.
/// </summary>
/// <remarks>
/// Both <c>Server</c> and <c>Agent</c> run as real, in-process hosts - real Kestrel, a real
/// <see cref="GrpcChannel"/> between them - rather than as separate OS processes, so this test
/// needs no image build and no <c>docker compose</c> orchestration of its own; only Postgres and
/// bagetter are containers. Requires <c>dbdeploy</c> on <c>PATH</c>, the same as every other
/// <c>[Category("Docker")]</c> test in this repo that touches Postgres directly.
/// </remarks>
[Category("Docker")]
[ClassDataSource<PostgreSqlFixture, BagetterFixture>(Shared = [SharedType.PerAssembly, SharedType.PerAssembly])]
public class InstallLoopDockerTests(PostgreSqlFixture postgres, BagetterFixture bagetter)
{
    private const string PackageId = "EtlPipelines.Samples.ArchiveToDatabase";

    // NuGetFeeds is a table every Docker-tagged test in this project shares (one Postgres
    // container, SharedType.PerAssembly) - see ManagementServiceDockerTests's own comment on the
    // same attribute. Clearing it before NuGetFeedSeeder runs (inside ServerApplication.StartAsync
    // below) is what guarantees GetFeedUrlsAsync hands the agent exactly the one real bagetter feed
    // this test just pushed a package to, regardless of what any other test left behind or what
    // order tests within this assembly happen to run in.
    [Test]
    [NotInParallel("NuGetFeeds")]
    public async Task InstallPackage_dispatches_to_the_connected_agent_and_ListInstalledPipelines_sees_the_result()
    {
        //arrange
        await SchemaDeployer.EnsureDeployedAsync(postgres.ConnectionString);
        await ClearNuGetFeedsAsync();
        var version = await PackAndPushAsync();

        await using var server = EtlPipelines.Server.ServerApplication.Build([
            "--Server:Port", "0",
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

            //act
            var installResponse = await client.InstallPackageAsync(new InstallPackageRequest { PackageId = PackageId, Version = version });

            //assert
            installResponse.PipelineNames.Should().BeEquivalentTo(["build-feed", "archive"]);

            var listResponse = await client.ListInstalledPipelinesAsync(new Empty());
            listResponse.Pipelines
                .Where(pipeline => pipeline.PackageId == PackageId)
                .Select(pipeline => pipeline.Name)
                .Should().BeEquivalentTo(["build-feed", "archive"]);
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
        var work = Directory.CreateTempSubdirectory("EtlPipelines.Server.Tests.InstallLoop.");

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

            // "<PackageId>.<version>" -> "<version>" - PackageId is the fixed prefix, so this is
            // simpler and more robust than parsing NuGetVersion out of an arbitrary file name.
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

    /// <summary>
    /// The real, connectable address to reach <paramref name="app"/> at: ListenAnyIP(0, ...) means
    /// <c>app.Urls</c> reports something like <c>http://[::]:54321</c> - a real bound address, but
    /// not one a client can dial (<c>[::]</c> is the IPv6 "any address" a server binds to, not a
    /// destination a client connects to) - so only the port survives, and 127.0.0.1 is what this
    /// test actually calls, same as every other loopback address ListenAnyIP also accepts.
    /// </summary>
    private static string GetBoundUrl(WebApplication app)
    {
        var boundAddress = app.Urls.FirstOrDefault() ?? throw new InvalidOperationException("Server did not report a bound URL.");
        return $"http://127.0.0.1:{new Uri(boundAddress).Port}";
    }

    private static GrpcChannel CreateChannel(string address)
    {
        // Cleartext HTTP/2 (h2c) - the same client-side opt-in AddAgentGrpcClient makes for the
        // agent's own connection, needed here too since this channel is constructed directly
        // rather than through DI.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return GrpcChannel.ForAddress(address);
    }

    /// <summary>
    /// Polls the server's own <see cref="AgentConnectionRegistry"/> - resolvable because this test
    /// runs the real Server in-process - until the agent this test just started has registered and
    /// subscribed, or <paramref name="timeout"/> elapses.
    /// </summary>
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
