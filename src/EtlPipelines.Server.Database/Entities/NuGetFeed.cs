namespace EtlPipelines.Server.Database.Entities;

/// <summary>
/// A NuGet-compatible feed <c>Server</c> resolves packages against. Always exactly one row in
/// phase 1 - the local bagetter URL - per SERVER.md's "Package feed" decision:
/// <see cref="Ordinal"/> is kept for a possible future multi-feed phase, unused while this table
/// has one row.
/// </summary>
public sealed class NuGetFeed
{
    public required Guid Id { get; init; }
    public required string Url { get; set; }
    public required int Ordinal { get; set; }
}
