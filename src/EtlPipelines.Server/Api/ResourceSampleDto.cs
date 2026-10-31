using System.Text.Json.Serialization;

namespace EtlPipelines.Server.Api;

/// <summary>One CPU/memory sample of a run's process, taken by the agent that runs it.</summary>
public sealed record ResourceSampleDto([property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime SampledAtUtc, double CpuPercent, long WorkingSetBytes);
