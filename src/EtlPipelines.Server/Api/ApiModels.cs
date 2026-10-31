namespace EtlPipelines.Server.Api;

/// <summary>An installed pipeline, with how it is doing - what the WebUI's pipelines page lists.</summary>
public sealed record PipelineDto(
    Guid Id,
    string Name,
    string PackageId,
    string PackageVersion,
    int ActiveRuns,
    RunSummaryDto? LastRun);

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

/// <summary>One stage of a run as the pipeline process reported it.</summary>
public sealed record StageDto(
    int Sequence,
    string Name,
    long RowsIn,
    long RowsOut,
    long RowsFailed,
    long ElapsedMs,
    string? ErrorCode,
    string? ErrorDescription);

/// <summary>One CPU/memory sample of a run's process, taken by the agent that runs it.</summary>
public sealed record ResourceSampleDto(DateTime SampledAt, double CpuPercent, long WorkingSetBytes);

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

/// <summary>An agent, with its host details and the runs it is working on right now.</summary>
public sealed record AgentDto(
    Guid Id,
    string MachineName,
    IReadOnlyList<string> Tags,
    string Version,
    string Status,
    DateTime LastHeartbeatAt,
    string? OsDescription,
    string? OsArchitecture,
    IReadOnlyList<string> IpAddresses,
    int? ProcessorCount,
    DateTime? StartedAt,
    long? UptimeSeconds,
    IReadOnlyList<RunSummaryDto> ActiveRuns);

/// <summary>What <c>POST /api/pipelines/{id}/runs</c> answers with.</summary>
public sealed record TriggerRunResponse(Guid RunId);
