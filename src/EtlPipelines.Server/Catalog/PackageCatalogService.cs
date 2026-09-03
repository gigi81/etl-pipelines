using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using NuGet.Versioning;

namespace EtlPipelines.Server.Catalog;

/// <summary>
/// Combines <c>Server.Database</c>'s catalog tables with the one bagetter feed to serve
/// <c>ManagementService</c>'s metadata-only RPCs (SERVER.md Phase 4) - installed pipelines come
/// from <c>Server.Database</c> alone; available packages and updates need the feed too.
/// </summary>
public sealed class PackageCatalogService(ServerDbContext context, INuGetFeedClient feedClient)
{
    /// <summary>Every pipeline a package currently installed has registered.</summary>
    public async Task<IReadOnlyList<InstalledPipelineInfo>> ListInstalledPipelinesAsync(CancellationToken cancellationToken) =>
        await context.Pipelines
            .AsNoTracking()
            .Select(pipeline => new InstalledPipelineInfo(
                pipeline.Id,
                pipeline.Name,
                pipeline.PackageVersion.Package.NugetPackageId,
                pipeline.PackageVersion.Version))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Every package the configured feed holds, optionally filtered by <paramref name="searchTerm"/>.</summary>
    public async Task<IReadOnlyList<AvailableFeedPackage>> ListAvailablePackagesAsync(
        string? searchTerm, CancellationToken cancellationToken)
    {
        var feedUrl = await GetFeedUrlAsync(cancellationToken).ConfigureAwait(false);
        if (feedUrl is null)
        {
            return [];
        }

        return await feedClient.SearchAsync(feedUrl, searchTerm, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every installed package whose feed's latest stable version is newer than what is currently
    /// installed. "What is currently installed" is each package's most recently installed
    /// <c>PackageVersions</c> row - <c>Status = "Installed"</c>, latest <c>InstalledAt</c>.
    /// </summary>
    public async Task<IReadOnlyList<PackageUpdateInfo>> ListUpdatesAsync(CancellationToken cancellationToken)
    {
        var feedUrl = await GetFeedUrlAsync(cancellationToken).ConfigureAwait(false);
        if (feedUrl is null)
        {
            return [];
        }

        var installed = await context.Packages
            .AsNoTracking()
            .Select(package => new
            {
                package.NugetPackageId,
                InstalledVersion = package.PackageVersions
                    .Where(version => version.Status == "Installed")
                    .OrderByDescending(version => version.InstalledAt)
                    .Select(version => version.Version)
                    .FirstOrDefault(),
            })
            .Where(package => package.InstalledVersion != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var updates = new List<PackageUpdateInfo>();

        foreach (var package in installed)
        {
            var latest = await feedClient
                .GetLatestVersionAsync(feedUrl, package.NugetPackageId, cancellationToken)
                .ConfigureAwait(false);

            if (latest is null)
            {
                continue;
            }

            // Filtered to non-null InstalledVersion above, but that survives the query, not the
            // C# nullable-flow analysis across it.
            var installedVersion = NuGetVersion.Parse(package.InstalledVersion!);
            if (latest > installedVersion)
            {
                updates.Add(new PackageUpdateInfo(package.NugetPackageId, package.InstalledVersion!, latest.ToNormalizedString()));
            }
        }

        return updates;
    }

    /// <summary>
    /// Resolves what version to install: <paramref name="requestedVersion"/> verbatim if given,
    /// otherwise the feed's latest stable version - "empty resolves to latest stable" per
    /// <c>InstallPackageRequest</c>'s own proto comment. Null if neither is available (nothing
    /// requested and nothing stable on the feed).
    /// </summary>
    public async Task<string?> ResolveVersionAsync(string packageId, string? requestedVersion, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(requestedVersion))
        {
            return requestedVersion;
        }

        var feedUrl = await GetFeedUrlAsync(cancellationToken).ConfigureAwait(false);
        if (feedUrl is null)
        {
            return null;
        }

        var latest = await feedClient.GetLatestVersionAsync(feedUrl, packageId, cancellationToken).ConfigureAwait(false);
        return latest?.ToNormalizedString();
    }

    /// <summary>
    /// Every feed URL <c>NuGetFeeds</c> currently holds - the <c>--add-source</c> argument(s) for
    /// the agent's <c>dotnet tool install</c>. In practice this is 0 or 1 rows (SERVER.md's
    /// "Package feed" decision), matching the proto's own <c>repeated string feed_urls</c> either way.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetFeedUrlsAsync(CancellationToken cancellationToken) =>
        await context.NuGetFeeds
            .AsNoTracking()
            .OrderBy(feed => feed.Ordinal)
            .Select(feed => feed.Url)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Records what an agent reported back for an install: a new <c>Packages</c> row the first
    /// time this NuGet package id is ever seen, always a new <c>PackageVersions</c> row, and -
    /// only when <paramref name="succeeded"/> - one <c>Pipelines</c> row per name the installed
    /// package's own <c>list</c> verb reported.
    /// </summary>
    /// <returns>The new <c>PackageVersions</c> row's id.</returns>
    public async Task<Guid> RecordInstallResultAsync(
        string packageId, string version, bool succeeded, IReadOnlyList<string> pipelineNames, CancellationToken cancellationToken)
    {
        var package = await context.Packages
            .SingleOrDefaultAsync(p => p.NugetPackageId == packageId, cancellationToken)
            .ConfigureAwait(false);

        if (package is null)
        {
            package = new Package { Id = Guid.NewGuid(), NugetPackageId = packageId, CreatedAt = DateTime.UtcNow };
            context.Packages.Add(package);
        }

        var packageVersion = new PackageVersion
        {
            Id = Guid.NewGuid(),
            PackageId = package.Id,
            Version = version,
            InstalledAt = DateTime.UtcNow,
            Status = succeeded ? "Installed" : "Failed",
        };
        context.PackageVersions.Add(packageVersion);

        if (succeeded)
        {
            foreach (var name in pipelineNames)
            {
                context.Pipelines.Add(new Pipeline
                {
                    Id = Guid.NewGuid(),
                    PackageVersionId = packageVersion.Id,
                    Name = name,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return packageVersion.Id;
    }

    private async Task<string?> GetFeedUrlAsync(CancellationToken cancellationToken) =>
        (await GetFeedUrlsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
}

/// <summary>One installed pipeline, joined out to the package/version that registered it.</summary>
public sealed record InstalledPipelineInfo(Guid PipelineId, string Name, string PackageId, string PackageVersion);

/// <summary>One installed package with a newer stable version available on the feed.</summary>
public sealed record PackageUpdateInfo(string PackageId, string InstalledVersion, string LatestVersion);
