namespace EtlPipelines.Server.Catalog;

/// <summary>One package on a feed, with every version it publishes.</summary>
public sealed record AvailableFeedPackage(string PackageId, IReadOnlyList<string> Versions);
