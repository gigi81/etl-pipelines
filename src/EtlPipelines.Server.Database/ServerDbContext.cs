using EtlPipelines.Server.Database.Configurations;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Database;

/// <summary>
/// The query/mapping layer over the schema <c>db/postgres</c>'s dbdeploy scripts own - see
/// SERVER.md's "Database schema management" decision: no <c>dotnet ef migrations</c>, ever, and
/// no <c>Migrations</c> folder. Every mapping is a hand-written
/// <see cref="IEntityTypeConfiguration{TEntity}"/> matching a table
/// <c>db/postgres/Server/InitialSchema.CreateCoreTables.Deploy.sql</c> already created.
/// </summary>
/// <remarks>
/// Not sealed: <c>EtlPipelines.Server.Database.Tests</c>' <c>SqliteServerDbContext</c> subclasses
/// this to own the SQLite connection its in-memory database depends on staying open - the one
/// place in this project a subclass makes sense, so nothing else should add another.
/// </remarks>
public class ServerDbContext(DbContextOptions<ServerDbContext> options) : DbContext(options)
{
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();
    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<StageResult> StageResults => Set<StageResult>();
    public DbSet<AgentResourceSample> AgentResourceSamples => Set<AgentResourceSample>();
    public DbSet<ConfigurationEntry> ConfigurationEntries => Set<ConfigurationEntry>();
    public DbSet<NuGetFeed> NuGetFeeds => Set<NuGetFeed>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PackageConfiguration());
        modelBuilder.ApplyConfiguration(new PackageVersionConfiguration());
        modelBuilder.ApplyConfiguration(new PipelineConfiguration());
        modelBuilder.ApplyConfiguration(new AgentConfiguration());
        modelBuilder.ApplyConfiguration(new RunConfiguration());
        modelBuilder.ApplyConfiguration(new StageResultConfiguration());
        modelBuilder.ApplyConfiguration(new AgentResourceSampleConfiguration());
        modelBuilder.ApplyConfiguration(new ConfigurationEntryConfiguration());
        modelBuilder.ApplyConfiguration(new NuGetFeedConfiguration());
    }
}
