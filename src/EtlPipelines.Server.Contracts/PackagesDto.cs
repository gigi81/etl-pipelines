namespace EtlPipelines.Server.Contracts;

/// <summary>What <c>GET /api/packages</c> answers with.</summary>
/// <param name="Packages">The packages, by id.</param>
/// <param name="FeedError">
/// Why the feed could not be browsed, when it could not: the installed packages are still listed, so
/// they can still be updated or uninstalled, but with nothing known about what the feed offers.
/// </param>
public sealed record PackagesDto(IReadOnlyList<PackageDto> Packages, string? FeedError);
