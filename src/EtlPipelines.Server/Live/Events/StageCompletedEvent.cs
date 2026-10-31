using EtlPipelines.Server.Contracts;

namespace EtlPipelines.Server.Live.Events;

/// <summary>A run completed one of its stages.</summary>
/// <param name="RunId">The run.</param>
/// <param name="StageId">The id of the row it is stored as.</param>
/// <param name="Stage">The stage's result.</param>
/// <param name="AtUtc">When it was reported.</param>
public sealed record StageCompletedEvent(Guid RunId, Guid StageId, StageDto Stage, DateTime AtUtc) : LiveEvent(AtUtc);
