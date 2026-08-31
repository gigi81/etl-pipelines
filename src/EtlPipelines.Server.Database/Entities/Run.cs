namespace EtlPipelines.Server.Database.Entities;

/// <summary>
/// One execution of a <see cref="Pipeline"/>. <see cref="Id"/> is the session id a launched
/// pipeline process authenticates back to <c>PipelineExecutionService</c> with - minted here,
/// before the process (or even the agent it will run on) exists.
/// </summary>
public sealed class Run
{
    public required Guid Id { get; init; }
    public required Guid PipelineId { get; init; }

    /// <summary>Null until dispatched - a run is queued before any agent claims it.</summary>
    public Guid? AgentId { get; set; }

    public required RunStatus Status { get; set; }
    public required DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public long? RowsRead { get; set; }
    public long? RowsWritten { get; set; }
    public long? RowsFailed { get; set; }
}
