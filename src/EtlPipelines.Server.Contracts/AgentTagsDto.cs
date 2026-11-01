namespace EtlPipelines.Server.Contracts;

/// <summary>The body of <c>PUT /api/agents/{id}/tags</c>.</summary>
/// <param name="Tags">
/// The tags to give the agent, replacing the ones a user gave it before (the ones the agent reports about itself are
/// not affected). Lower-cased; letters, digits and <c>. _ : / -</c>.
/// </param>
public sealed record AgentTagsDto(IReadOnlyList<string> Tags);
