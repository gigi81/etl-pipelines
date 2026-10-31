namespace EtlPipelines.Server.Live;

/// <summary>What one liveness sweep did.</summary>
/// <param name="AgentsMarkedOffline">Agents that had stopped heartbeating.</param>
/// <param name="RunsMarkedLost">Their runs that had not finished.</param>
public readonly record struct LiveSweepResult(int AgentsMarkedOffline, int RunsMarkedLost);
