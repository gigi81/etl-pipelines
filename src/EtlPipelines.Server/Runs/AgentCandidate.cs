namespace EtlPipelines.Server.Runs;

/// <summary>A connected agent a run could be sent to.</summary>
/// <param name="Id">The agent.</param>
/// <param name="Tags">All its tags.</param>
/// <param name="HasPackage">Whether it is known to have the package version installed already.</param>
/// <param name="ActiveRuns">How many runs it is carrying out now.</param>
public sealed record AgentCandidate(Guid Id, IReadOnlyList<string> Tags, bool HasPackage, int ActiveRuns);
