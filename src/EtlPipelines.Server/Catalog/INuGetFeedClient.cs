using NuGet.Versioning;

namespace EtlPipelines.Server.Catalog;

/// <summary>
/// Browses a NuGet-compatible feed for package metadata - search and available versions, never a
/// download. See SERVER.md's "Package feed" decision: there is exactly one feed URL in practice
/// (the local bagetter), which is why every method here takes it as a plain parameter rather than
/// baking it into a stateful client - <see cref="Catalog.PackageCatalogService"/> is what reads
/// that URL from <c>NuGetFeeds</c> and passes it along.
/// </summary>
public interface INuGetFeedClient
{
    /// <summary>
    /// Every package matching <paramref name="searchTerm"/>, or every package on the feed when it
    /// is null/empty - the same contract a NuGet search box has.
    /// </summary>
    Task<IReadOnlyList<AvailableFeedPackage>> SearchAsync(
        string feedUrl, string? searchTerm, CancellationToken cancellationToken);

    /// <summary>The latest stable version of <paramref name="packageId"/> on the feed, or null if it isn't there.</summary>
    Task<NuGetVersion?> GetLatestVersionAsync(string feedUrl, string packageId, CancellationToken cancellationToken);
}

/// <summary>One package on a feed, with every version it publishes.</summary>
public sealed record AvailableFeedPackage(string PackageId, IReadOnlyList<string> Versions);
