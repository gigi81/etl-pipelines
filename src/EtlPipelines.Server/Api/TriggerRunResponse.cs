namespace EtlPipelines.Server.Api;

/// <summary>What <c>POST /api/pipelines/{id}/runs</c> answers with.</summary>
public sealed record TriggerRunResponse(Guid RunId);
