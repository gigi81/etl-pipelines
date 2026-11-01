namespace EtlPipelines.Server.Live.Events;

/// <summary>
/// The finished runs of a pipeline - and their stages and resource samples - were deleted. Runs in
/// flight are not touched. What is deleted is every run of the pipeline that is finished at the point
/// this event is reached in the ordered log, which is why it can be replayed as a set-based delete
/// (older runs are not held in memory).
/// </summary>
public sealed record PipelineHistoryClearedEvent(Guid PipelineId, DateTime AtUtc) : LiveEvent(AtUtc);
