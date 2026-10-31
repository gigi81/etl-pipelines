using System.Text.Json;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database.Entities;

namespace EtlPipelines.Server.Live;

/// <summary>An agent as <see cref="LiveState"/> holds it. Immutable - an update replaces it.</summary>
internal sealed record LiveAgent(
    Guid Id,
    string MachineName,
    IReadOnlyList<string> Tags,
    string Version,
    string Status,
    DateTime LastHeartbeatAtUtc,
    JsonElement? Info)
{
    /// <summary>Copies <paramref name="agent"/> (a database row).</summary>
    public static LiveAgent From(Agent agent) =>
        new(
            agent.Id,
            agent.MachineName,
            agent.Tags,
            agent.Version,
            agent.Status,
            DateTime.SpecifyKind(agent.LastHeartbeatAt, DateTimeKind.Utc),
            ParseInfo(agent.Info));

    /// <summary>The stored <c>Agents.Info</c> as JSON; <see langword="null"/> when there is none, or it is not a JSON object.</summary>
    internal static JsonElement? ParseInfo(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The agent as the API shows it, with the runs it is working on right now.</summary>
    public AgentDto ToDto(DateTimeOffset now, IReadOnlyList<RunSummaryDto> activeRuns) =>
        new(
            Id,
            MachineName,
            Tags,
            Version,
            Status,
            LastHeartbeatAtUtc,
            Info,
            Status == "Online" ? UptimeSeconds(now) : null,
            activeRuns);

    /// <summary>Seconds from <c>info.startedAtUtc</c> to <paramref name="now"/>, when the agent reported a start time.</summary>
    private long? UptimeSeconds(DateTimeOffset now) =>
        Info is { } info
            && info.TryGetProperty("startedAtUtc", out var startedAt)
            && startedAt.ValueKind == JsonValueKind.String
            && startedAt.TryGetDateTimeOffset(out var started)
                ? Math.Max(0, (long)(now - started).TotalSeconds)
                : null;
}
