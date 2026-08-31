namespace EtlPipelines.Server.Database.Entities;

/// <summary>An agent process that has registered with the server (<c>AgentService.RegisterAgent</c>).</summary>
public sealed class Agent
{
    public required Guid Id { get; init; }
    public required string MachineName { get; init; }
    public IReadOnlyList<string> Tags { get; set; } = [];
    public required string Version { get; set; }
    public required AgentStatus Status { get; set; }
    public required DateTimeOffset LastHeartbeatAt { get; set; }
}
