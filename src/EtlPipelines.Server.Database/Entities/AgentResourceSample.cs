namespace EtlPipelines.Server.Database.Entities;

/// <summary>
/// One CPU/memory sample an agent's <c>ResourceMonitor</c> (Phase 5) took of a <see cref="Run"/>
/// it is executing, off <c>Process.TotalProcessorTime</c>/<c>WorkingSet64</c>.
/// </summary>
public sealed class AgentResourceSample
{
    public required Guid Id { get; init; }
    public required Guid RunId { get; init; }
    public required Guid AgentId { get; init; }
    public required DateTimeOffset SampledAt { get; init; }
    public required double CpuPercent { get; init; }
    public required long WorkingSetBytes { get; init; }
}
