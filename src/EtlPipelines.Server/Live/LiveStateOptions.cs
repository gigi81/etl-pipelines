namespace EtlPipelines.Server.Live;

/// <summary>Configuration for <see cref="LiveState"/>, from the <c>"LiveState"</c> section.</summary>
public sealed class LiveStateOptions
{
    /// <summary>The configuration section these options are read from.</summary>
    public const string SectionName = "LiveState";

    /// <summary>
    /// How often events that have happened since the last time are written to the database - if
    /// there are any. The most a hard crash can lose is this much. 5 seconds by default.
    /// </summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(5);
}
