namespace EtlPipelines.Server.Contracts;

/// <summary>The body of <c>PUT /api/pipelines/{id}/display-name</c>.</summary>
/// <param name="DisplayName">The name to show for the pipeline; empty or null goes back to the pipeline's own name.</param>
public sealed record PipelineDisplayNameDto(string? DisplayName);
