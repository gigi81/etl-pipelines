namespace EtlPipelines.Server.Live.Events;

/// <summary>An agent stopped heartbeating and was marked offline.</summary>
public sealed record AgentOfflineEvent(Guid AgentId, DateTime AtUtc) : LiveEvent(AtUtc);
