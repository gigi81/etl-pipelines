namespace EtlPipelines.Server.Contracts;

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
