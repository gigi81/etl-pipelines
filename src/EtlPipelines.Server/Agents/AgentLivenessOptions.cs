namespace EtlPipelines.Server.Agents;

/// <summary>Configuration <see cref="AgentLivenessMonitor"/> reads from its own <c>"AgentLiveness"</c> configuration section.</summary>
public sealed class AgentLivenessOptions
{
    public const string SectionName = "AgentLiveness";

    /// <summary>
    /// How long since <c>Agents.LastHeartbeatAt</c> before an agent is marked <c>Offline</c> and
    /// any <c>Runs</c> row it still owns is marked <c>AgentLost</c> (SERVER.md Phase 8). Generous
    /// relative to <c>AgentRegistration</c>'s own 10-second heartbeat interval - a few missed
    /// beats (a slow GC pause, a brief network blip) should not flip an agent offline, only one
    /// that has genuinely stopped.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often <see cref="AgentLivenessMonitor"/> checks for agents past <see cref="Timeout"/>.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
}
