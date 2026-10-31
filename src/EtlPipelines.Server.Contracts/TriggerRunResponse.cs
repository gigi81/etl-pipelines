namespace EtlPipelines.Server.Contracts;

/// <summary>What <c>POST /api/pipelines/{id}/runs</c> answers with.</summary>
public sealed record TriggerRunResponse(Guid RunId);
