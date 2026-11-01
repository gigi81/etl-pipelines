namespace EtlPipelines.Agent;

/// <summary>Configuration this agent reads from its own <c>"Agent"</c> configuration section.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// Where packages get installed - <c>&lt;CacheDirectory&gt;/&lt;packageId&gt;/&lt;version&gt;</c>,
    /// matching Phase 1's own verification. Becomes the <c>agent-cache</c> volume in Phase 7.
    /// </summary>
    public string CacheDirectory { get; set; } = "agent-cache";

    /// <summary>The gRPC endpoint <c>AgentService</c> is reachable at.</summary>
    public string ServerUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// The name this agent is shown by. Defaults to the machine name, which in Docker is a container id
    /// that changes whenever the container is recreated - set a name to keep it stable.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Free-form tags this agent registers with, e.g. <c>"linux"</c>, <c>"gpu"</c>. Empty by default.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];

    /// <summary>
    /// <see cref="CacheDirectory"/>'s total size cap, in bytes - <see cref="CacheEvictor"/> deletes
    /// the least-recently-used installed package version(s) once this is exceeded (SERVER.md Phase
    /// 8: "agent-cache... grows unboundedly through Phase 7"). 5 GiB by default - generous for a
    /// handful of installed tool packages, small next to what a machine an operator dedicates to
    /// running an agent typically has to spare.
    /// </summary>
    public long CacheSizeCapBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// How often a running pipeline process's CPU and working set are sampled and reported to the
    /// Server (<c>AgentResourceSamples</c>). 15 seconds by default.
    /// </summary>
    public TimeSpan ResourceSampleInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many pipeline runs this agent carries out at once; further ones wait their turn. 4 by default. Installing a
    /// package is not counted: installs run alongside runs (one at a time among themselves).
    /// </summary>
    public int MaxConcurrentRuns { get; set; } = 4;

    /// <summary>How often the agent tells the Server it is alive. 10 seconds by default; the Server's timeout is 30.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the agent waits before reconnecting after losing the Server. 1 second by default, doubling at every
    /// failed attempt up to <see cref="ReconnectMaxDelay"/>.
    /// </summary>
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest the agent waits between attempts to reconnect. 30 seconds by default.</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the agent waits before trying again to tell the Server a run started or ended, when it cannot be
    /// reached (a restart, say). 1 second by default, doubling up to 30 seconds, for up to 8 attempts - about 90
    /// seconds in all.
    /// </summary>
    public TimeSpan ReportRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often <see cref="CacheEvictor"/> checks <see cref="CacheDirectory"/> against <see cref="CacheSizeCapBytes"/>.</summary>
    public TimeSpan CacheEvictionInterval { get; set; } = TimeSpan.FromMinutes(30);
}
