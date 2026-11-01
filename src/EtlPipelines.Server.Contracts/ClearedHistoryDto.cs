namespace EtlPipelines.Server.Contracts;

/// <summary>What clearing a pipeline's history answers with.</summary>
/// <param name="Deleted">How many finished runs were deleted.</param>
public sealed record ClearedHistoryDto(int Deleted);
