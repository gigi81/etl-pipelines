namespace EtlPipelines.Server.Live;

/// <summary>
/// A notice that something in <see cref="LiveState"/> changed - not the new value, which a
/// subscriber reads from the state when it gets to the notice, so a burst of changes to the same
/// thing is delivered once, with its latest state.
/// </summary>
/// <param name="Kind">What changed.</param>
/// <param name="Id">The run or agent id; <see cref="Guid.Empty"/> for <see cref="LiveChangeKind.Pipelines"/>.</param>
public readonly record struct LiveChange(LiveChangeKind Kind, Guid Id);
