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

    /// <summary>How often <see cref="CacheEvictor"/> checks <see cref="CacheDirectory"/> against <see cref="CacheSizeCapBytes"/>.</summary>
    public TimeSpan CacheEvictionInterval { get; set; } = TimeSpan.FromMinutes(30);
}
