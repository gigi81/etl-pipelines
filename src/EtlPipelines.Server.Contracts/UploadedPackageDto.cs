namespace EtlPipelines.Server.Contracts;

/// <summary>What uploading a package answers with: what the file turned out to be.</summary>
/// <param name="Id">The package id.</param>
/// <param name="Version">The package version.</param>
public sealed record UploadedPackageDto(string Id, string Version);
