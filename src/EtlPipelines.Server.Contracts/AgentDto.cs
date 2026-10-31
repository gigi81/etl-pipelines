using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtlPipelines.Server.Contracts;

/// <summary>An agent, with its host details and the runs it is working on right now.</summary>
/// <param name="Info">
/// What the agent reported about its host when it registered, exactly as stored (<c>Agents.Info</c>):
/// a free-form JSON object, so a detail added to the agent later reaches the UI without a change here.
/// Today: <c>osDescription</c>, <c>osArchitecture</c>, <c>processorCount</c>, <c>ipAddresses</c> (an
/// array) and <c>startedAtUtc</c> (ISO 8601, UTC). <see langword="null"/> for an agent that never reported any.
/// </param>
/// <param name="UptimeSeconds">Seconds since <c>info.startedAtUtc</c>, for an online agent that reported it.</param>
public sealed record AgentDto(
    Guid Id,
    string MachineName,
    IReadOnlyList<string> Tags,
    string Version,
    string Status,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime LastHeartbeatAtUtc,
    JsonElement? Info,
    long? UptimeSeconds,
    IReadOnlyList<RunSummaryDto> ActiveRuns);
