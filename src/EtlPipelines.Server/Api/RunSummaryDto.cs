using System.Text.Json.Serialization;

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
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime RequestedAtUtc,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime? StartedAtUtc,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime? CompletedAtUtc,
    int? ExitCode,
    long? RowsRead,
    long? RowsWritten,
    long? RowsFailed,
    int StagesCompleted,
    int? StagesExpected);
