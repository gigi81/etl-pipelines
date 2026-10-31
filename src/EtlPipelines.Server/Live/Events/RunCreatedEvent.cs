namespace EtlPipelines.Server.Live.Events;

/// <summary>A run was created and dispatched to an agent; its status is <c>Dispatched</c>.</summary>
/// <param name="RunId">The run's id - also the session id the launched process reports with.</param>
/// <param name="PipelineId">The pipeline it runs.</param>
/// <param name="PipelineName">The pipeline's name. Not stored with the run (the database joins it): it is here for the in-memory run.</param>
/// <param name="PackageId">The package it belongs to. Likewise only for the in-memory run.</param>
/// <param name="PackageVersion">The package version. Likewise only for the in-memory run.</param>
/// <param name="AgentId">The agent it was dispatched to.</param>
/// <param name="ExpectedStages">The stage names of the pipeline's last successful run. Likewise only for the in-memory run.</param>
/// <param name="AtUtc">When it was requested.</param>
public sealed record RunCreatedEvent(
    Guid RunId,
    Guid PipelineId,
    string PipelineName,
    string PackageId,
    string PackageVersion,
    Guid? AgentId,
    IReadOnlyList<string> ExpectedStages,
    DateTime AtUtc) : LiveEvent(AtUtc);
