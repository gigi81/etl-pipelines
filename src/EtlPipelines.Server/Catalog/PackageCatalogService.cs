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
            .Where(pipeline => pipeline.PackageVersion.Status == PackageVersionStatus.Installed)
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
                    .Where(version => version.Status == PackageVersionStatus.Installed)
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
    /// time this NuGet package id is ever seen, a <c>PackageVersions</c> row the first time this
    /// version is, and - only when <paramref name="succeeded"/> - a <c>Pipelines</c> row for each
    /// name the installed package's own <c>list</c> verb reported that the version does not
    /// already have.
    /// </summary>
    /// <remarks>
    /// Idempotent per (package, version), deliberately: the same version is legitimately installed
    /// more than once - by a second agent (every agent keeps its own copy), or again after an
    /// agent's cache evicted it - and <c>UX_PackageVersions_PackageId_Version</c> rejects a second
    /// row outright. Inserting unconditionally, as this used to, threw on every such re-install,
    /// failing <c>AgentService.ReportInstallResult</c> and with it the installing agent's whole
    /// host. A failed re-install never downgrades a version that is already <c>Installed</c>:
    /// that one agent's failure says nothing about the copies other agents already have.
    /// </remarks>
    /// <returns>The <c>PackageVersions</c> row's id - the existing one on a re-install.</returns>
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

        var packageVersion = await context.PackageVersions
            .Include(v => v.Pipelines)
            .SingleOrDefaultAsync(v => v.PackageId == package.Id && v.Version == version, cancellationToken)
            .ConfigureAwait(false);

        if (packageVersion is null)
        {
            packageVersion = new PackageVersion
            {
                Id = Guid.NewGuid(),
                PackageId = package.Id,
                Version = version,
                InstalledAt = DateTime.UtcNow,
                Status = succeeded ? PackageVersionStatus.Installed : PackageVersionStatus.Failed,
            };
            context.PackageVersions.Add(packageVersion);
        }
        else if (succeeded)
        {
            packageVersion.Status = PackageVersionStatus.Installed;
            packageVersion.InstalledAt = DateTime.UtcNow;
        }

        if (succeeded)
        {
            var existingNames = packageVersion.Pipelines.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in pipelineNames)
            {
                if (!existingNames.Add(name))
                {
                    continue;
                }

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

    /// <summary>
    /// Marks every installed version of <paramref name="packageId"/> <see cref="PackageVersionStatus.Uninstalled"/>.
    /// The rows stay - <c>Runs</c> reference their pipelines - but they are no longer listed or executable.
    /// </summary>
    /// <returns>False when no version of <paramref name="packageId"/> is installed.</returns>
    public async Task<bool> UninstallAsync(string packageId, CancellationToken cancellationToken)
    {
        var installed = await InstalledVersions(packageId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (installed.Count == 0)
        {
            return false;
        }

        foreach (var version in installed)
        {
            version.Status = PackageVersionStatus.Uninstalled;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Every installed version of <paramref name="packageId"/> other than
    /// <paramref name="currentVersion"/> becomes <see cref="PackageVersionStatus.Superseded"/> -
    /// what makes <c>UpdatePackage</c> an update rather than a second install alongside the first.
    /// </summary>
    public async Task SupersedeOtherVersionsAsync(string packageId, string currentVersion, CancellationToken cancellationToken)
    {
        var older = await InstalledVersions(packageId)
            .Where(version => version.Version != currentVersion)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var version in older)
        {
            version.Status = PackageVersionStatus.Superseded;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="packageId"/> <paramref name="version"/> if it is currently installed: its
    /// <c>PackageVersions</c> id and pipeline names. Null otherwise.
    /// </summary>
    public async Task<InstalledVersionInfo?> FindInstalledVersionAsync(string packageId, string version, CancellationToken cancellationToken) =>
        await InstalledVersions(packageId)
            .AsNoTracking()
            .Where(v => v.Version == version)
            .Select(v => new InstalledVersionInfo(v.Id, v.Pipelines.Select(p => p.Name).ToList()))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Whether any version of <paramref name="packageId"/> is currently installed.</summary>
    public Task<bool> IsInstalledAsync(string packageId, CancellationToken cancellationToken) =>
        InstalledVersions(packageId).AnyAsync(cancellationToken);

    private IQueryable<PackageVersion> InstalledVersions(string packageId) =>
        context.PackageVersions.Where(version =>
            version.Package.NugetPackageId == packageId && version.Status == PackageVersionStatus.Installed);

    private async Task<string?> GetFeedUrlAsync(CancellationToken cancellationToken) =>
        (await GetFeedUrlsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
}

/// <summary>One installed pipeline, joined out to the package/version that registered it.</summary>
public sealed record InstalledPipelineInfo(Guid PipelineId, string Name, string PackageId, string PackageVersion);

/// <summary>One installed package version: its <c>PackageVersions</c> id and the pipelines it registered.</summary>
public sealed record InstalledVersionInfo(Guid PackageVersionId, IReadOnlyList<string> PipelineNames);

/// <summary>One installed package with a newer stable version available on the feed.</summary>
public sealed record PackageUpdateInfo(string PackageId, string InstalledVersion, string LatestVersion);
