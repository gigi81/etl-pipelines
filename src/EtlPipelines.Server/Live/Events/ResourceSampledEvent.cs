namespace EtlPipelines.Server.Live.Events;

/// <summary>An agent sampled the CPU and memory of a run's process.</summary>
/// <param name="RunId">The run.</param>
/// <param name="AgentId">The agent that sampled it.</param>
/// <param name="SampleId">The id of the row it is stored as.</param>
/// <param name="CpuPercent">CPU use, in percent.</param>
/// <param name="WorkingSetBytes">The process's working set.</param>
/// <param name="AtUtc">When it was sampled.</param>
public sealed record ResourceSampledEvent(
    Guid RunId, Guid AgentId, Guid SampleId, double CpuPercent, long WorkingSetBytes, DateTime AtUtc) : LiveEvent(AtUtc);
