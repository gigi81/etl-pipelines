namespace EtlPipelines.Server.Live.Events;

/// <summary>A user changed the tags of an agent.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="AssignedTags">All of the tags given to it by a user (already normalized) - the whole list, not a change to it.</param>
/// <param name="AtUtc">When.</param>
public sealed record AgentTagsChangedEvent(Guid AgentId, IReadOnlyList<string> AssignedTags, DateTime AtUtc) : LiveEvent(AtUtc);
