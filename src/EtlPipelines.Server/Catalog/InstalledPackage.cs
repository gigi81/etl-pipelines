namespace EtlPipelines.Server.Catalog;

/// <summary>What installing or updating a package answers with: the version now installed and the pipelines it provides.</summary>
/// <param name="PackageVersionId">The id of the installed version's catalog row.</param>
/// <param name="Version">The version installed.</param>
/// <param name="PipelineNames">The names of the pipelines the package registers.</param>
public sealed record InstalledPackage(Guid PackageVersionId, string Version, IReadOnlyList<string> PipelineNames);
