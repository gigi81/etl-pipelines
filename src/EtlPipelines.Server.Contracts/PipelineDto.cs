namespace EtlPipelines.Server.Contracts;

/// <summary>An installed pipeline, with how it is doing - what the WebUI's pipelines page lists.</summary>
public sealed record PipelineDto(
    Guid Id,
    string Name,
    string? DisplayName,
    string PackageId,
    string PackageVersion,
    int ActiveRuns,
    RunSummaryDto? LastRun);
