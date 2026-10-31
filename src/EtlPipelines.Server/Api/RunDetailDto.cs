namespace EtlPipelines.Server.Api;

/// <summary>A run with everything the run page shows - and what <c>/api/runs/{id}/stream</c> pushes.</summary>
/// <param name="Run">The run itself.</param>
/// <param name="Stages">The stages that have completed so far, in order.</param>
/// <param name="ExpectedStages">
/// The stage names of this pipeline's last successful run, in order - what the run is expected to
/// go through, so the UI can draw the stages that have not completed yet. Empty for a pipeline that
/// has never succeeded.
/// </param>
/// <param name="ResourceSamples">The most recent resource samples, oldest first.</param>
public sealed record RunDetailDto(
    RunSummaryDto Run,
    IReadOnlyList<StageDto> Stages,
    IReadOnlyList<string> ExpectedStages,
    IReadOnlyList<ResourceSampleDto> ResourceSamples);
