namespace EtlPipelines.Server.Catalog;

/// <summary>The values <c>PackageVersions.Status</c> takes.</summary>
public static class PackageVersionStatus
{
    /// <summary>Listed by <c>ListInstalledPipelines</c> and executable - the only status that is.</summary>
    public const string Installed = "Installed";

    /// <summary>Every agent that tried to install it reported a failure.</summary>
    public const string Failed = "Failed";

    /// <summary>Retired by <c>UninstallPackage</c>. Installing the version again brings it back.</summary>
    public const string Uninstalled = "Uninstalled";

    /// <summary>Retired by <c>UpdatePackage</c> in favour of a newer version. Installing it again brings it back.</summary>
    public const string Superseded = "Superseded";
}
