namespace EtlPipelines.Server.Live.Events;

/// <summary>An agent heartbeated, so it is online.</summary>
public sealed record AgentHeartbeatEvent(Guid AgentId, DateTime AtUtc) : LiveEvent(AtUtc);
