namespace EtlPipelines.Server.Live.Events;

/// <summary>A run's process started: it is now <c>Running</c>, and has a start time if it had none.</summary>
public sealed record RunStartedEvent(Guid RunId, DateTime AtUtc) : LiveEvent(AtUtc);
