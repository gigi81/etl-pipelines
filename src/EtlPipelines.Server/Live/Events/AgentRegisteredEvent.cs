namespace EtlPipelines.Server.Live.Events;

/// <summary>An agent registered with the server.</summary>
/// <param name="AgentId">The id the server issued.</param>
/// <param name="MachineName">The agent's machine name.</param>
/// <param name="Tags">The tags it registered with.</param>
/// <param name="Version">The agent's version.</param>
/// <param name="InfoJson">What it reported about its host, as a JSON object (<c>Agents.Info</c>); null if nothing.</param>
/// <param name="AtUtc">When it registered; also its first heartbeat.</param>
public sealed record AgentRegisteredEvent(
    Guid AgentId,
    string MachineName,
    IReadOnlyList<string> Tags,
    string Version,
    string? InfoJson,
    DateTime AtUtc) : LiveEvent(AtUtc);
