namespace EtlPipelines.Server.Contracts;

/// <summary>An installed pipeline, with how it is doing - what the WebUI's pipelines page lists.</summary>
/// <param name="Assignment">Which agents may run it; empty when any may. Only agents that exist are listed.</param>
public sealed record PipelineDto(
    Guid Id,
    string Name,
    string? DisplayName,
    string PackageId,
    string PackageVersion,
    int ActiveRuns,
    RunSummaryDto? LastRun,
    PipelineAssignmentDto Assignment);
