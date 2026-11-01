using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Api;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Secrets;
using EtlPipelines.Server.Services;
using Grpc.Core;
using Moq.Protected;
using EtlPipelines.Server.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>
/// The REST API (<see cref="ApiEndpoints.MapEtlApi"/>) on a real Kestrel listener with an ephemeral
/// port, over a shared-cache in-memory SQLite database - shared-cache so a test can write to the
/// database while a request (the live stream's polling loop, say) reads it, each on its own
/// connection, as they would against Postgres.
/// </summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly WebApplication _app;

    private ApiTestHost(WebApplication app, SqliteConnection keepAlive, string connectionString, HttpClient client, AgentConnectionRegistry connections, LiveState live, Mock<INuGetFeedClient> feed)
    {
        _app = app;
        _keepAlive = keepAlive;
        _connectionString = connectionString;
        Client = client;
        Connections = connections;
        Live = live;
        FeedClient = feed;
    }

    public HttpClient Client { get; }

    public AgentConnectionRegistry Connections { get; }

    /// <summary>The package feed the server browses and pushes to - a mock the test scripts.</summary>
    public Mock<INuGetFeedClient> FeedClient { get; }

    /// <summary>The server's in-memory state, which is what the API reads.</summary>
    public LiveState Live { get; }

    public static async Task<ApiTestHost> StartAsync()
    {
        var connectionString = $"DataSource=file:api-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var connections = new AgentConnectionRegistry();
        var live = new LiveState(TimeProvider.System, NullLogger<LiveState>.Instance);
        builder.Services.AddDbContext<ServerDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(connections);
        builder.Services.AddSingleton(live);
        var feed = new Mock<INuGetFeedClient>();
        builder.Services.AddSingleton(feed.Object);
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        builder.Services.AddScoped<SecretsStore>();
        builder.Services.AddScoped<PackageCatalogService>();
        builder.Services.AddScoped<PackageOperations>();
        builder.Services.AddScoped<PackageUploader>();
        builder.Services.AddScoped(provider => new RunDispatcher(
            provider.GetRequiredService<ServerDbContext>(), connections, live, "http://server.test"));

        var app = builder.Build();
        app.MapEtlApi();
        await app.StartAsync();

        using (var scope = app.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            await context.Database.EnsureCreatedAsync();
            await live.LoadAsync(context, CancellationToken.None);
        }

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };

        return new ApiTestHost(app, keepAlive, connectionString, client, connections, live, feed);
    }

    /// <summary>
    /// Loads the server's in-memory state from the database as it is now - what a restart does. The
    /// seeding helpers write the database directly, as a previous run of the server would have left
    /// it, so each ends with this; a test that writes the database itself calls it too.
    /// </summary>
    public async Task ReloadAsync()
    {
        await using var context = NewContext();
        await Live.LoadAsync(context, CancellationToken.None);
    }

    /// <summary>Gives the server a package feed to install from, as the seeded compose stack has.</summary>
    public async Task SeedFeedAsync(string url = "http://feed.test/v3/index.json")
    {
        await using var context = NewContext();
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = url, Ordinal = 0 });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Plays the agent a package install was sent to: takes the work item it was given, installs
    /// nothing, and reports back that it succeeded - with <paramref name="pipelineNames"/> - or failed.
    /// Returns the work item, so a test can see what the Server asked for.
    /// </summary>
    public async Task<InstallPackageWorkItem> AnswerInstallAsync(
        string agentId, Channel<WorkItem> channel, bool succeeded = true, params string[] pipelineNames)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var workItem = await channel.Reader.ReadAsync(timeout.Token);

        await using var context = NewContext();
        var agentService = new AgentServiceImpl(Connections, new PackageCatalogService(context, FeedClient.Object), Live);
        var report = new ReportInstallResultRequest
        {
            AgentId = agentId,
            WorkItemId = workItem.WorkItemId,
            Succeeded = succeeded,
            Error = succeeded ? string.Empty : "dotnet tool install failed",
        };
        report.PipelineNames.AddRange(pipelineNames);

        var call = new Mock<ServerCallContext> { CallBase = true };
        call.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        await agentService.ReportInstallResult(report, call.Object);

        return workItem.InstallPackage;
    }

    /// <summary>A fresh context on the same database the API reads.</summary>
    public ServerDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ServerDbContext>().UseSqlite(_connectionString).Options);

    public async Task<T> GetAsync<T>(string path) =>
        (await Client.GetFromJsonAsync<T>(path, Json))!;

    /// <summary>Installs a pipeline (package, version, pipeline rows - reusing any that exist) and returns its id.</summary>
    public async Task<Guid> SeedPipelineAsync(string name, string packageId = "EtlPipelines.Samples.Test", string version = "1.0.0")
    {
        await using var context = NewContext();

        var package = await context.Packages.SingleOrDefaultAsync(p => p.NugetPackageId == packageId);
        if (package is null)
        {
            package = new Package { Id = Guid.NewGuid(), NugetPackageId = packageId, CreatedAt = DateTime.UtcNow };
            context.Packages.Add(package);
        }

        var packageVersion = await context.PackageVersions.SingleOrDefaultAsync(v => v.PackageId == package.Id && v.Version == version);
        if (packageVersion is null)
        {
            packageVersion = new PackageVersion
            {
                Id = Guid.NewGuid(), PackageId = package.Id, Version = version, InstalledAt = DateTime.UtcNow, Status = PackageVersionStatus.Installed,
            };
            context.PackageVersions.Add(packageVersion);
        }

        var pipeline = new Pipeline { Id = Guid.NewGuid(), PackageVersionId = packageVersion.Id, Name = name, CreatedAt = DateTime.UtcNow };
        context.Pipelines.Add(pipeline);
        await context.SaveChangesAsync();
        await ReloadAsync();
        return pipeline.Id;
    }

    public async Task<Guid> SeedAgentAsync(
        string machineName = "agent-01", string status = "Online", DateTime? startedAt = null, string[]? tags = null)
    {
        await using var context = NewContext();
        var agent = new Database.Entities.Agent
        {
            Id = Guid.NewGuid(),
            MachineName = machineName,
            Tags = [.. tags ?? ["linux"]], AssignedTags = [],
            Version = "1.2.3",
            Status = status,
            LastHeartbeatAt = DateTime.UtcNow,
            Info = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["osDescription"] = "Ubuntu 24.04",
                ["osArchitecture"] = "X64",
                ["processorCount"] = 8,
                ["ipAddresses"] = new[] { "10.0.0.7", "2001:db8::7" },
                ["startedAtUtc"] = startedAt?.ToUniversalTime().ToString("O"),
                ["somethingAddedLater"] = "kept as is",
            }),
        };
        context.Agents.Add(agent);
        await context.SaveChangesAsync();
        await ReloadAsync();
        return agent.Id;
    }

    public async Task<Guid> SeedRunAsync(
        Guid pipelineId, string status, Guid? agentId = null, DateTime? requestedAt = null, params (string Name, bool Failed)[] stages)
    {
        await using var context = NewContext();
        var requested = requestedAt ?? DateTime.UtcNow;
        var terminal = status is "Succeeded" or "Failed" or "AgentLost";
        var run = new Run
        {
            Id = Guid.NewGuid(),
            PipelineId = pipelineId,
            AgentId = agentId,
            Status = status,
            RequestedAt = requested,
            StartedAt = requested,
            CompletedAt = terminal ? requested.AddSeconds(30) : null,
        };
        context.Runs.Add(run);

        for (var index = 0; index < stages.Length; index++)
        {
            context.StageResults.Add(new StageResult
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Sequence = index,
                Name = stages[index].Name,
                RowsIn = 10,
                RowsOut = 9,
                RowsFailed = 1,
                ElapsedMs = 100,
                ErrorCode = stages[index].Failed ? "E1" : null,
                ErrorDescription = stages[index].Failed ? "boom" : null,
            });
        }

        await context.SaveChangesAsync();
        await ReloadAsync();
        return run.Id;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }
}
