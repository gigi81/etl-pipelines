namespace EtlPipelines.Server.Database.Entities;

/// <summary>A NuGet package the catalog knows about - one row per distinct package id.</summary>
public sealed class Package
{
    public required Guid Id { get; init; }

    /// <summary>The package id as bagetter/NuGet know it, e.g. "EtlPipelines.Samples.CsvToDatabase".</summary>
    public required string NugetPackageId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
