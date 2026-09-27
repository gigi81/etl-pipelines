using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Runs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Server.Tests.Agents;

/// <summary>
/// Fast tests for <see cref="AgentLivenessMonitor.SweepAsync"/> - SERVER.md Phase 8's own
/// instruction ("fast tests for the heartbeat timeout (fake clock)"). Drives the timeout decision
/// with a fake "now" (<see cref="FakeTimeProvider"/>) rather than a real elapsed wait, and a real
/// SQLite-backed <see cref="ServerDbContext"/> behind a genuine <see cref="IServiceScopeFactory"/>
/// (the same per-tick-scope shape <see cref="AgentLivenessMonitor"/> itself uses in production),
/// so the EF Core query <see cref="AgentLivenessMonitor.SweepAsync"/> runs is exercised for real,
/// not mocked away.
/// </summary>
[Category("Server")]
public class AgentLivenessMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task An_agent_past_the_timeout_is_marked_Offline()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var scopeFactory = await CreateScopeFactoryAsync();
        var agentId = Guid.NewGuid();
        await SeedAsync(scopeFactory, context => context.Agents.Add(MakeAgent(agentId, now.UtcDateTime - Timeout - TimeSpan.FromSeconds(1))));

        var monitor = CreateMonitor(scopeFactory, new FakeTimeProvider(now), new RunStatusStore());

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        var agent = await GetAsync(scopeFactory, context => context.Agents.SingleAsync(a => a.Id == agentId));
        agent.Status.Should().Be("Offline");
    }

    [Test]
    public async Task An_agent_within_the_timeout_is_left_Online()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var scopeFactory = await CreateScopeFactoryAsync();
        var agentId = Guid.NewGuid();
        await SeedAsync(scopeFactory, context => context.Agents.Add(MakeAgent(agentId, now.UtcDateTime - Timeout + TimeSpan.FromSeconds(5))));

        var monitor = CreateMonitor(scopeFactory, new FakeTimeProvider(now), new RunStatusStore());

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        var agent = await GetAsync(scopeFactory, context => context.Agents.SingleAsync(a => a.Id == agentId));
        agent.Status.Should().Be("Online");
    }

    [Test]
    [Arguments("Queued")]
    [Arguments("Dispatched")]
    [Arguments("Running")]
    public async Task A_Run_still_active_on_a_timed_out_agent_is_marked_AgentLost_and_published(string activeStatus)
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var scopeFactory = await CreateScopeFactoryAsync();
        var agentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await SeedAsync(scopeFactory, context =>
        {
            context.Agents.Add(MakeAgent(agentId, now.UtcDateTime - Timeout - TimeSpan.FromSeconds(1)));
            context.Runs.Add(MakeRun(context, runId, agentId, activeStatus));
        });

        var statusStore = new RunStatusStore();
        var reader = statusStore.Subscribe(runId);
        var monitor = CreateMonitor(scopeFactory, new FakeTimeProvider(now), statusStore);

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        var run = await GetAsync(scopeFactory, context => context.Runs.SingleAsync(r => r.Id == runId));
        run.Status.Should().Be("AgentLost");
        run.CompletedAt.Should().Be(now.UtcDateTime);

        var published = await reader.ReadAsync(CancellationToken.None);
        published.RunCompleted.Status.Should().Be(RunCompleted.Types.Status.AgentLost);
    }

    [Test]
    public async Task A_Run_already_terminal_is_left_alone_even_if_its_agent_timed_out()
    {
        //arrange
        var now = DateTimeOffset.UtcNow;
        var scopeFactory = await CreateScopeFactoryAsync();
        var agentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await SeedAsync(scopeFactory, context =>
        {
            context.Agents.Add(MakeAgent(agentId, now.UtcDateTime - Timeout - TimeSpan.FromSeconds(1)));
            context.Runs.Add(MakeRun(context, runId, agentId, "Succeeded"));
        });

        var monitor = CreateMonitor(scopeFactory, new FakeTimeProvider(now), new RunStatusStore());

        //act
        await monitor.SweepAsync(CancellationToken.None);

        //assert
        var run = await GetAsync(scopeFactory, context => context.Runs.SingleAsync(r => r.Id == runId));
        run.Status.Should().Be("Succeeded");
    }

    // Fully qualified - within this file's own EtlPipelines.Server.Tests.Agents namespace, the
    // bare name "Agent" resolves to the sibling EtlPipelines.Agent *project* namespace (this test
    // project also references it), not this entity type - the same collision
    // ManagementServiceImplTests works around the same way.
    private static Database.Entities.Agent MakeAgent(Guid id, DateTime lastHeartbeatAt) => new()
    {
        Id = id, MachineName = "agent-01", Tags = [], Version = "1.0.0", Status = "Online", LastHeartbeatAt = lastHeartbeatAt,
    };

    /// <summary>A minimal Package/PackageVersion/Pipeline chain, matching <c>ManagementServiceImplTests</c>' own seeding shape - a <c>Runs</c> row needs a real <c>Pipelines</c> FK to attach to.</summary>
    private static Run MakeRun(ServerDbContext context, Guid runId, Guid agentId, string status)
    {
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var pipelineId = Guid.NewGuid();

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.Add(new Pipeline { Id = pipelineId, PackageVersionId = versionId, Name = "csv-to-database", CreatedAt = DateTime.UtcNow });

        return new Run { Id = runId, PipelineId = pipelineId, AgentId = agentId, Status = status, RequestedAt = DateTime.UtcNow };
    }

    private static AgentLivenessMonitor CreateMonitor(IServiceScopeFactory scopeFactory, TimeProvider clock, RunStatusStore statusStore) =>
        new(scopeFactory, Microsoft.Extensions.Options.Options.Create(new AgentLivenessOptions { Timeout = Timeout, PollInterval = TimeSpan.FromSeconds(10) }),
            statusStore, clock, NullLogger<AgentLivenessMonitor>.Instance);

    private static async Task SeedAsync(IServiceScopeFactory scopeFactory, Action<ServerDbContext> seed)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        seed(context);
        await context.SaveChangesAsync();
    }

    private static async Task<T> GetAsync<T>(IServiceScopeFactory scopeFactory, Func<ServerDbContext, Task<T>> query)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ServerDbContext>());
    }

    /// <summary>
    /// A real SQLite-in-memory-backed <see cref="ServerDbContext"/> registered scoped behind a real
    /// <see cref="IServiceScopeFactory"/> - the connection is kept open for this provider's whole
    /// lifetime (an in-memory SQLite database disappears the moment its one open connection
    /// closes), the same reason <c>SqliteServerDbContext</c> owns its own connection.
    /// </summary>
    private static async Task<IServiceScopeFactory> CreateScopeFactoryAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ServerDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.EnsureCreatedAsync();
        }

        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> is fixed at construction - what lets these tests assert a 30-second timeout without a real 30-second wait.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
