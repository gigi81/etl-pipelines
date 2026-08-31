using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;
using NuGet.Versioning;

namespace EtlPipelines.Server.Tests.Catalog;

/// <summary>
/// Fast tests against a SQLite-backed <c>ServerDbContext</c> and a mocked
/// <see cref="INuGetFeedClient"/> - SERVER.md Phase 4's own instruction for this project. See
/// <c>EtlPipelines.Server.Database.Tests</c>' <c>[Category("Docker")]</c> suite for the real
/// Postgres/EF-mapping proof this is not a substitute for.
/// </summary>
[Category("Server")]
public class PackageCatalogServiceTests
{
    [Test]
    public async Task Installed_pipelines_join_out_to_their_package_and_version()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        var packageId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.ArchiveToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion { Id = versionId, PackageId = packageId, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" });
        context.Pipelines.AddRange(
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "build-feed", CreatedAt = DateTime.UtcNow },
            new Pipeline { Id = Guid.NewGuid(), PackageVersionId = versionId, Name = "archive", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var service = new PackageCatalogService(context, Mock.Of<INuGetFeedClient>());

        //act
        var pipelines = await service.ListInstalledPipelinesAsync(CancellationToken.None);

        //assert
        pipelines.Should().HaveCount(2);
        pipelines.Should().OnlyContain(pipeline => pipeline.PackageId == "EtlPipelines.Samples.ArchiveToDatabase" && pipeline.PackageVersion == "1.0.0");
        pipelines.Select(pipeline => pipeline.Name).Should().BeEquivalentTo(["build-feed", "archive"]);
    }

    [Test]
    public async Task Available_packages_is_empty_when_no_feed_is_configured()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        var feedClient = new Mock<INuGetFeedClient>();
        var service = new PackageCatalogService(context, feedClient.Object);

        //act
        var packages = await service.ListAvailablePackagesAsync(searchTerm: null, CancellationToken.None);

        //assert - nothing in NuGetFeeds, so nothing to browse; the feed client is never even asked.
        packages.Should().BeEmpty();
        feedClient.Verify(client => client.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Available_packages_are_searched_against_the_configured_feed_url()
    {
        //arrange
        await using var context = SqliteServerDbContext.Create();
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = "http://nuget:5000/v3/index.json", Ordinal = 0 });
        await context.SaveChangesAsync();

        var feedClient = new Mock<INuGetFeedClient>();
        feedClient
            .Setup(client => client.SearchAsync("http://nuget:5000/v3/index.json", "Csv", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AvailableFeedPackage("EtlPipelines.Samples.CsvToDatabase", ["1.0.0"])]);

        var service = new PackageCatalogService(context, feedClient.Object);

        //act
        var packages = await service.ListAvailablePackagesAsync("Csv", CancellationToken.None);

        //assert
        packages.Should().ContainSingle(package => package.PackageId == "EtlPipelines.Samples.CsvToDatabase");
    }

    [Test]
    public async Task An_installed_package_is_an_update_when_the_feed_has_a_newer_stable_version()
    {
        //arrange
        await using var context = await SeedInstalledPackageAsync("1.0.0");
        var feedClient = new Mock<INuGetFeedClient>();
        feedClient
            .Setup(client => client.GetLatestVersionAsync(It.IsAny<string>(), "EtlPipelines.Samples.CsvToDatabase", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NuGetVersion.Parse("1.1.0"));

        var service = new PackageCatalogService(context, feedClient.Object);

        //act
        var updates = await service.ListUpdatesAsync(CancellationToken.None);

        //assert
        updates.Should().ContainSingle();
        updates[0].PackageId.Should().Be("EtlPipelines.Samples.CsvToDatabase");
        updates[0].InstalledVersion.Should().Be("1.0.0");
        updates[0].LatestVersion.Should().Be("1.1.0");
    }

    [Test]
    public async Task An_installed_package_is_not_an_update_when_it_is_already_the_feed_s_latest()
    {
        //arrange
        await using var context = await SeedInstalledPackageAsync("1.1.0");
        var feedClient = new Mock<INuGetFeedClient>();
        feedClient
            .Setup(client => client.GetLatestVersionAsync(It.IsAny<string>(), "EtlPipelines.Samples.CsvToDatabase", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NuGetVersion.Parse("1.1.0"));

        var service = new PackageCatalogService(context, feedClient.Object);

        //act
        var updates = await service.ListUpdatesAsync(CancellationToken.None);

        //assert
        updates.Should().BeEmpty();
    }

    private static async Task<SqliteServerDbContext> SeedInstalledPackageAsync(string installedVersion)
    {
        var context = SqliteServerDbContext.Create();
        var packageId = Guid.NewGuid();
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = "http://nuget:5000/v3/index.json", Ordinal = 0 });
        context.Packages.Add(new Package { Id = packageId, NugetPackageId = "EtlPipelines.Samples.CsvToDatabase", CreatedAt = DateTime.UtcNow });
        context.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(), PackageId = packageId, Version = installedVersion,
            InstalledAt = DateTime.UtcNow, Status = "Installed",
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return context;
    }
}
