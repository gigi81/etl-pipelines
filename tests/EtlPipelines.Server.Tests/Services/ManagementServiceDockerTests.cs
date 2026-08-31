using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using EtlPipelines.Management.V1;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
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

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.ArchiveToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.AddRange(
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "build-feed", CreatedAt = DateTime.UtcNow },
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "archive", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        //act
        var response = await service.ListInstalledPipelines(new Empty(), TestServerCallContext());

        //assert
        response.Pipelines.Select(pipeline => pipeline.Name).Should().BeEquivalentTo(["build-feed", "archive"]);
    }

    [Test]
    public async Task ListAvailablePackages_asks_the_feed_client_for_whatever_url_NuGetFeeds_holds()
    {
        //arrange
        await using var context = await DeploySchemaAndCreateContextAsync();
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
    public async Task ListUpdates_compares_the_real_installed_row_against_the_feed()
    {
        //arrange
        await using var context = await DeploySchemaAndCreateContextAsync();
        var packageId = Guid.NewGuid();
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
        ServerDbContext context, INuGetFeedClient? feedClient = null, IDataProtectionProvider? dataProtectionProvider = null) =>
        new(
            new PackageCatalogService(context, feedClient ?? Mock.Of<INuGetFeedClient>()),
            new SecretsStore(context, dataProtectionProvider ?? Mock.Of<IDataProtectionProvider>()));

    private static ServerCallContext TestServerCallContext()
    {
        var context = new Mock<ServerCallContext> { CallBase = true };
        context.Protected().Setup<CancellationToken>("CancellationTokenCore").Returns(CancellationToken.None);
        return context.Object;
    }

    // Every test in this class shares one Postgres container ([ClassDataSource<PostgreSqlFixture>
    // (Shared = SharedType.PerAssembly)]), so `dbdeploy deploy` must run exactly once for it, not
    // once per test - a second `deploy` against an already-deployed database fails outright
    // ("relation \"Packages\" already exists"), unlike a real dbdeploy run where re-deploying an
    // up-to-date database is a normal no-op (there is nothing left in main.csv to apply the second
    // time; the failure here is specific to concurrently/repeatedly staging the very same "_Init"
    // step against a database that has already recorded it as deployed within this single
    // temporary --path). A lock plus a cached Task is enough: only one test's call actually
    // deploys, and every other test's call awaits that same Task instead of starting its own.
    private static Task? _schemaDeployTask;
    private static readonly Lock DeployLock = new();

    private Task EnsureSchemaDeployedAsync()
    {
        lock (DeployLock)
        {
            _schemaDeployTask ??= DeploySchemaAsync();
        }

        return _schemaDeployTask;
    }

    private async Task<ServerDbContext> DeploySchemaAndCreateContextAsync()
    {
        await EnsureSchemaDeployedAsync();

        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql(Fixture.ConnectionString)
            .Options;

        return new ServerDbContext(options);
    }

    private async Task DeploySchemaAsync()
    {
        var work = Directory.CreateTempSubdirectory("EtlPipelines.Server.Tests.");

        try
        {
            await StageScriptsAsync(work.FullName);

            BufferedCommandResult deploy;
            try
            {
                deploy = await Cli.Wrap("dbdeploy")
                    .WithArguments(["deploy", "--path", work.FullName])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync();
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException(
                    "'dbdeploy' could not be started - install it with " +
                    "'dotnet tool install --global dbdeploy' before running [Category(\"Docker\")] tests.",
                    exception);
            }

            if (deploy.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"dbdeploy deploy failed: {(string.IsNullOrWhiteSpace(deploy.StandardError) ? deploy.StandardOutput : deploy.StandardError)}");
            }
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

    private async Task StageScriptsAsync(string workDirectory)
    {
        var sourceServerDirectory = Path.Combine(RepositoryPaths.DbDirectory, "server");
        var destinationServerDirectory = Directory.CreateDirectory(Path.Combine(workDirectory, "server"));

        foreach (var script in Directory.GetFiles(sourceServerDirectory))
        {
            File.Copy(script, Path.Combine(destinationServerDirectory.FullName, Path.GetFileName(script)));
        }

        File.Copy(
            Path.Combine(RepositoryPaths.DbDirectory, "main.csv"),
            Path.Combine(workDirectory, "main.csv"));

        var settings = JsonSerializer.Serialize(new
        {
            global = new { defaultProvider = "postgreSql", scriptTimeout = 600 },
            databases = new Dictionary<string, object>
            {
                ["server"] = new { connectionString = Fixture.ConnectionString },
            },
        });

        await File.WriteAllTextAsync(Path.Combine(workDirectory, "dbsettings.json"), settings);
    }
}
