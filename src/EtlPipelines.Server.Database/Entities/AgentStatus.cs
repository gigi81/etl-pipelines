namespace EtlPipelines.Server.Database.Entities;

/// <summary>Whether an <see cref="Agent"/> is heartbeating. Phase 8 flips this after N missed heartbeats.</summary>
public enum AgentStatus
{
    Online,
    Offline,
}
