namespace EtlPipelines.Server.Contracts;

/// <summary>A package on the feed and/or installed, with what can be done with it - one row of the packages page.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Versions">Every version the feed publishes, newest first; empty if the feed does not have the package.</param>
/// <param name="LatestStableVersion">The newest version that is not a prerelease, if any.</param>
/// <param name="InstalledVersion">The version installed (the newest, if several are); null when it is not installed.</param>
/// <param name="UpdateAvailable">Whether the feed has a stable version newer than the installed one.</param>
/// <param name="Pipelines">The pipelines the installed version provides.</param>
public sealed record PackageDto(
    string Id,
    IReadOnlyList<string> Versions,
    string? LatestStableVersion,
    string? InstalledVersion,
    bool UpdateAvailable,
    IReadOnlyList<string> Pipelines);
