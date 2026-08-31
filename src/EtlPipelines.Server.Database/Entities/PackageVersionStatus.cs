namespace EtlPipelines.Server.Database.Entities;

/// <summary>Where a <see cref="PackageVersion"/> is in the install-delegation flow (Phase 5).</summary>
public enum PackageVersionStatus
{
    Installing,
    Installed,
    Failed,
}
