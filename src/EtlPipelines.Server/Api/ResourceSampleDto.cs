namespace EtlPipelines.Server.Api;

/// <summary>One CPU/memory sample of a run's process, taken by the agent that runs it.</summary>
public sealed record ResourceSampleDto(DateTime SampledAt, double CpuPercent, long WorkingSetBytes);
