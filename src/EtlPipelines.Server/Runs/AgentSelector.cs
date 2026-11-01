namespace EtlPipelines.Server.Runs;

/// <summary>Chooses the agent a run goes to.</summary>
public static class AgentSelector
{
    /// <summary>
    /// The best of the candidates <paramref name="assignment"/> allows: the one with the fewest runs going,
    /// because an agent runs only so many pipelines at once and the rest wait on it while another agent
    /// sat idle. Among equally busy agents, one that already has the package installed
    /// (it can start at once); then the lowest id, so the choice is the same every time.
    /// <see langword="null"/> when none is allowed.
    /// </summary>
    public static AgentCandidate? Choose(IEnumerable<AgentCandidate> candidates, PipelineAssignment assignment) =>
        candidates
            .Where(candidate => assignment.Allows(candidate.Id, candidate.Tags))
            .OrderBy(candidate => candidate.ActiveRuns)
            .ThenByDescending(candidate => candidate.HasPackage)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();
}
