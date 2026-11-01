namespace EtlPipelines.Server.Live;

/// <summary>What choosing an agent for a run needs to know about one: its tags (all of them) and how busy it is.</summary>
/// <param name="Id">The agent.</param>
/// <param name="Tags">The tags it reported plus the ones a user gave it.</param>
/// <param name="ActiveRuns">How many runs it is carrying out now.</param>
public sealed record AgentLoad(Guid Id, IReadOnlyList<string> Tags, int ActiveRuns);
