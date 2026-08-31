namespace EtlPipelines.Server.Database.Entities;

/// <summary>
/// One nameable pipeline within a <see cref="PackageVersion"/> - a child of the version, not the
/// package directly, since one package can register more than one:
/// <c>EtlPipelines.Samples.ArchiveToDatabase</c>'s own <c>build-feed</c>/<c>archive</c> split is
/// the proof. <c>ListInstalledPipelines</c>/<c>ExecutePipeline</c> (management.v1) both operate at
/// this granularity, matching how <c>PipelineRunner.Find(name)</c>
/// (<c>src/EtlPipelines.Hosting/PipelineRunner.cs</c>) already does a linear scan by name within
/// one process.
/// </summary>
public sealed class Pipeline
{
    public required Guid Id { get; init; }
    public required Guid PackageVersionId { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
