namespace EtlPipelines.Server.Database.Entities;

/// <summary>
/// The server's own view of a <see cref="Run"/> - the full lifecycle, including states no
/// pipeline process ever reports about itself (compare
/// <c>pipeline_execution.v1.ReportRunResultRequest.Outcome</c>, which only carries the two a
/// process can report - <see cref="Succeeded"/>/<see cref="Failed"/>).
/// </summary>
public enum RunStatus
{
    Queued,
    Dispatched,
    Running,
    Succeeded,
    Failed,

    /// <summary>Assigned by the server (Phase 8), never by the process or the agent itself.</summary>
    AgentLost,
}
