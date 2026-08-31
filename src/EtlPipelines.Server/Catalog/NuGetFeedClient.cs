using System.Collections.Concurrent;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace EtlPipelines.Server.Catalog;

/// <summary>Wraps <c>NuGet.Protocol</c>'s search/find-package resources. See <see cref="INuGetFeedClient"/>.</summary>
/// <remarks>
/// <see cref="Repository.Factory.GetCoreV3"/> does no network I/O by itself - resolving a feed's
/// service index only happens the first time a resource is actually asked for - so the
/// <see cref="SourceRepository"/> per feed URL is cached rather than rebuilt on every call; in
/// practice there is exactly one feed URL, so this cache never holds more than one entry.
/// </remarks>
public sealed class NuGetFeedClient : INuGetFeedClient
{
    private static readonly SourceCacheContext CacheContext = new();

    private readonly ConcurrentDictionary<string, SourceRepository> _repositories = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<AvailableFeedPackage>> SearchAsync(
        string feedUrl, string? searchTerm, CancellationToken cancellationToken)
    {
        var resource = await GetRepository(feedUrl)
            .GetResourceAsync<PackageSearchResource>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"'{feedUrl}' does not support package search.");

        var results = await resource.SearchAsync(
            searchTerm ?? string.Empty,
            new SearchFilter(includePrerelease: true),
            skip: 0,
            take: 1000,
            NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);

        var packages = new List<AvailableFeedPackage>();

        foreach (var result in results)
        {
            var versions = await result.GetVersionsAsync().ConfigureAwait(false);
            packages.Add(new AvailableFeedPackage(
                result.Identity.Id,
                versions.Select(version => version.Version.ToNormalizedString()).ToList()));
        }

        return packages;
    }

    /// <inheritdoc />
    public async Task<NuGetVersion?> GetLatestVersionAsync(string feedUrl, string packageId, CancellationToken cancellationToken)
    {
        var resource = await GetRepository(feedUrl)
            .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"'{feedUrl}' does not support find-package-by-id.");

        var versions = await resource
            .GetAllVersionsAsync(packageId, CacheContext, NullLogger.Instance, cancellationToken)
            .ConfigureAwait(false);

        return versions
            .Where(version => !version.IsPrerelease)
            .OrderByDescending(version => version)
            .FirstOrDefault();
    }

    private SourceRepository GetRepository(string feedUrl) =>
        _repositories.GetOrAdd(feedUrl, Repository.Factory.GetCoreV3);
}
