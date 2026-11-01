namespace EtlPipelines.Server.Contracts;

/// <summary>What installing or updating a package answers with.</summary>
/// <param name="PackageId">The package.</param>
/// <param name="Version">The version now installed.</param>
/// <param name="PipelineNames">The pipelines it provides.</param>
public sealed record PackageInstallResultDto(string PackageId, string Version, IReadOnlyList<string> PipelineNames);
