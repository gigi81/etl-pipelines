using EtlPipelines.Server.Database;
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

    private async Task<string?> GetFeedUrlAsync(CancellationToken cancellationToken) =>
        await context.NuGetFeeds
            .AsNoTracking()
            .OrderBy(feed => feed.Ordinal)
            .Select(feed => feed.Url)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>One installed pipeline, joined out to the package/version that registered it.</summary>
public sealed record InstalledPipelineInfo(Guid PipelineId, string Name, string PackageId, string PackageVersion);

/// <summary>One installed package with a newer stable version available on the feed.</summary>
public sealed record PackageUpdateInfo(string PackageId, string InstalledVersion, string LatestVersion);
