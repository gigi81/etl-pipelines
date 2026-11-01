namespace EtlPipelines.Server.Live;

/// <summary>What asking to remove an agent came to.</summary>
public enum AgentRemovalResult
{
    /// <summary>The agent was removed.</summary>
    Removed,

    /// <summary>No such agent is known.</summary>
    NotFound,

    /// <summary>The agent is online. Only an agent that has stopped heartbeating can be removed.</summary>
    StillOnline,

    /// <summary>The agent still has runs in flight.</summary>
    HasActiveRuns,
}
