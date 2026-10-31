namespace EtlPipelines.Server.Api;

/// <summary>One run, without its per-stage detail - what the run lists show.</summary>
public sealed record RunSummaryDto(
    Guid Id,
    Guid PipelineId,
    string PipelineName,
    string PackageId,
    string PackageVersion,
    Guid? AgentId,
    string? AgentName,
    string Status,
    DateTime RequestedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    int? ExitCode,
    long? RowsRead,
    long? RowsWritten,
    long? RowsFailed,
    int StagesCompleted,
    int? StagesExpected);
