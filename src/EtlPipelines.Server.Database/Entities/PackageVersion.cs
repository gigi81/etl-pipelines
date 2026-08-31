namespace EtlPipelines.Server.Database.Entities;

/// <summary>One installed (or installing, or failed) version of a <see cref="Package"/>.</summary>
public sealed class PackageVersion
{
    public required Guid Id { get; init; }
    public required Guid PackageId { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset InstalledAt { get; init; }
    public required PackageVersionStatus Status { get; set; }
}
