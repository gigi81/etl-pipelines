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
/// The full <c>ManagementService</c> surface Phase 4 (SERVER.md) actually implements, proven
/// against a real, dbdeploy-deployed Postgres container rather than SQLite -
/// <see cref="ManagementServiceImplTests"/> covers the same RPCs' proto-mapping fast; this is the
/// one that proves the whole stack (real schema, real EF Core queries) agrees.
/// </summary>
/// <remarks>
/// No bagetter container here - SERVER.md's Phase 4 only asks for this suite to prove the
/// surface against "a real Postgres container", and <see cref="INuGetFeedClient"/> is exactly the
/// seam that lets <see cref="ListAvailablePackages"/>/<see cref="ListUpdates"/> be proven here
/// with a mock standing in for bagetter, while everything else in the request stays real.
/// Requires <c>dbdeploy</c> on <c>PATH</c>, the same as
/// <c>EtlPipelines.Server.Database.Tests.ServerDatabaseDockerTests</c>.
/// </remarks>
[Category("Docker")]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public class ManagementServiceDockerTests(PostgreSqlFixture fixture)
{
    private PostgreSqlFixture Fixture { get; } = fixture;

    [Test]
    public async Task ListInstalledPipelines_reads_a_real_deployed_schema()
    {
        //arrange
        await using var context = await DeploySchemaAndCreateContextAsync();
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        // A synthetic id, not a real sample's - NuGetFeeds isn't the only table Docker tests in
        // this project share (one Postgres container, SharedType.PerAssembly): this test used to
        // hardcode "EtlPipelines.Samples.ArchiveToDatabase" with pipelines "build-feed"/"archive",
        // which collided for real with InstallLoopDockerTests actually installing that exact
        // sample - ListInstalledPipelines has no per-test scoping, so its response is whatever the
        // whole table holds. Filtering the assertion below to this fixture's own package id is
        // what makes this test correct regardless of what else is in the table, rather than merely
        // avoiding today's one known collision.
        var nugetPackageId = $"Test.Fixture.{Guid.NewGuid():N}";

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = nugetPackageId, CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.AddRange(
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "build-feed", CreatedAt = DateTime.UtcNow },
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "archive", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        //act
        var response = await service.ListInstalledPipelines(new Empty(), TestServerCallContext());

        //assert
        response.Pipelines
            .Where(pipeline => pipeline.PackageId == nugetPackageId)
            .Select(pipeline => pipeline.Name)
            .Should().BeEquivalentTo(["build-feed", "archive"]);
    }

    // NuGetFeeds is a table every Docker-tagged test in this project shares (one Postgres
    // container, SharedType.PerAssembly) - this test and ListUpdates_... below are the only ones
    // that write to it, so [NotInParallel("NuGetFeeds")] plus clearing it first is what keeps them
    // from leaving a stale row for each other, or for InstallLoopDockerTests (whose whole point is
    // that GetFeedUrlsAsync returns exactly the one real bagetter feed it pushed a package to -
    // caught for real when a leftover "http://nuget:5000/v3/index.json" row from this test made
    // the agent's own `dotnet tool install` fail against a URL nothing was ever listening on).
    [Test]
    [NotInParallel("NuGetFeeds")]
    public async Task ListAvailablePackages_asks_the_feed_client_for_whatever_url_NuGetFeeds_holds()
    {
        //arrange
        await using var context = await DeploySchemaAndCreateContextAsync();
        context.NuGetFeeds.RemoveRange(await context.NuGetFeeds.ToListAsync());
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = "http://nuget:5000/v3/index.json", Ordinal = 0 });
        await context.SaveChangesAsync();

        var feedClient = new Mock<INuGetFeedClient>();
        feedClient
            .Setup(client => client.SearchAsync("http://nuget:5000/v3/index.json", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AvailableFeedPackage("EtlPipelines.Samples.CsvToDatabase", ["1.0.0"])]);

        var service = CreateService(context, feedClient.Object);

        //act
        var response = await service.ListAvailablePackages(new ListAvailablePackagesRequest(), TestServerCallContext());

        //assert
        response.Packages.Should().ContainSingle(package => package.PackageId == "EtlPipelines.Samples.CsvToDatabase");
    }

    [Test]
    [NotInParallel("NuGetFeeds")]
    public async Task ListUpdates_compares_the_real_installed_row_against_the_feed()
    {
        //arrange
        await using var context = await DeploySchemaAndCreateContextAsync();
        var packageId = Guid.NewGuid();
        context.NuGetFeeds.RemoveRange(await context.NuGetFeeds.ToListAsync());
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = "http://nuget:5000/v3/index.json", Ordinal = 0 });
        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = Guid.NewGuid(), PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        await context.SaveChangesAsync();

        var feedClient = new Mock<INuGetFeedClient>();
        feedClient
            .Setup(client => client.GetLatestVersionAsync(It.IsAny<string>(), "EtlPipelines.Samples.CsvToDatabase", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NuGet.Versioning.NuGetVersion.Parse("1.1.0"));

        var service = CreateService(context, feedClient.Object);

        //act
        var response = await service.ListUpdates(new Empty(), TestServerCallContext());

        //assert
        response.Updates.Should().ContainSingle(update =>
            update.PackageId == "EtlPipelines.Samples.CsvToDatabase" &&
            update.InstalledVersion == "1.0.0" &&
            update.LatestVersion == "1.1.0");
    }

    [Test]
    public async Task SetConfigurationEntry_round_trips_through_a_real_IDataProtector()
    {
        //arrange
        await using var context = await DeploySchemaAndCreateContextAsync();
        // A real, ephemeral (in-memory-keyed) protector rather than a mock here - this test's
        // point is the whole stack, and Data Protection needs no external infrastructure to
        // exercise for real the way Postgres does. One instance, shared by both the write and the
        // read below: each EphemeralDataProtectionProvider mints its own fresh in-memory key, so
        // - unlike PersistKeysToFileSystem's real key ring - a second instance could never
        // unprotect what a first one wrote, which is expected and not what this test is about.
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        var service = CreateService(context, dataProtectionProvider: dataProtectionProvider);

        //act
        await service.SetConfigurationEntry(
            new SetConfigurationEntryRequest { Key = "ConnectionStrings:sales", Value = "super-secret" },
            TestServerCallContext());

        //assert - never stored as plaintext, but reads back correctly through the same protector.
        var stored = await context.ConfigurationEntries.SingleAsync(entry => entry.Key == "ConnectionStrings:sales");
        System.Text.Encoding.UTF8.GetString(stored.EncryptedValue).Should().NotBe("super-secret");

        var secretsStore = new SecretsStore(context, dataProtectionProvider);
        (await secretsStore.GetAsync("ConnectionStrings:sales", CancellationToken.None)).Should().Be("super-secret");
    }

    private static ManagementServiceImpl CreateService(
        ServerDbContext context,
        INuGetFeedClient? feedClient = null,
        IDataProtectionProvider? dataProtectionProvider = null,
        AgentConnectionRegistry? connections = null)
    {
        var registry = connections ?? new AgentConnectionRegistry();

        return new ManagementServiceImpl(
            new PackageCatalogService(context, feedClient ?? Mock.Of<INuGetFeedClient>()),
            new SecretsStore(context, dataProtectionProvider ?? Mock.Of<IDataProtectionProvider>()),
            registry,
            new RunDispatcher(context, registry, "http://localhost:5000"),
            new RunStatusStore());
    }

    private static ServerCallContext TestServerCallContext()
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context.Object;
    }

    private async Task<ServerDbContext> DeploySchemaAndCreateContextAsync()
    {
        await SchemaDeployer.EnsureDeployedAsync(Fixture.ConnectionString);

        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .Options;

        return new ServerDbContext(options);
    }
}
