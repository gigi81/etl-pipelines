namespace EtlPipelines.Server.Contracts;

/// <summary>
/// Which agents may run a pipeline - the body of <c>PUT /api/pipelines/{id}/assignment</c>, and what a pipeline
/// listing carries. Both lists empty means any agent. Otherwise an agent may run it if it has any of
/// <paramref name="Tags"/> or is one of <paramref name="AgentIds"/>.
/// </summary>
/// <param name="Tags">Tags: every agent that has one of them may run the pipeline.</param>
/// <param name="AgentIds">Agents named explicitly.</param>
public sealed record PipelineAssignmentDto(IReadOnlyList<string> Tags, IReadOnlyList<Guid> AgentIds);
