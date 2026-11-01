namespace EtlPipelines.Server.Live.Events;

/// <summary>
/// An offline agent was removed. It is no longer listed; its row stays, marked <c>Removed</c>, so the
/// runs it carried out keep the machine name they ran on.
/// </summary>
public sealed record AgentRemovedEvent(Guid AgentId, DateTime AtUtc) : LiveEvent(AtUtc);
