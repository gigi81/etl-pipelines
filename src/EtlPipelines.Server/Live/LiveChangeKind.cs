namespace EtlPipelines.Server.Live;

/// <summary>What a <see cref="LiveChange"/> is about.</summary>
public enum LiveChangeKind
{
    /// <summary>A run was created or changed: its status, stages or resource samples.</summary>
    Run,

    /// <summary>An agent registered, heartbeated, went offline - or the runs it is working on changed.</summary>
    Agent,

    /// <summary>The set of installed pipelines changed (a package was installed, updated or uninstalled).</summary>
    Pipelines,
}
