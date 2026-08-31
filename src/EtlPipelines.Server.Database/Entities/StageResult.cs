namespace EtlPipelines.Server.Database.Entities;

/// <summary>One completed stage of a <see cref="Run"/> - one row per <c>ReportStageResult</c> call.</summary>
public sealed class StageResult
{
    public required Guid Id { get; init; }
    public required Guid RunId { get; init; }
    public required int Sequence { get; init; }
    public required string Name { get; init; }
    public required long RowsIn { get; init; }
    public required long RowsOut { get; init; }
    public required long RowsFailed { get; init; }
    public required long ElapsedMs { get; init; }

    /// <summary>Both null when the stage succeeded.</summary>
    public string? ErrorCode { get; init; }

    public string? ErrorDescription { get; init; }
}
