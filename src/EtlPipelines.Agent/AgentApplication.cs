using EtlPipelines.Agent.GrpcClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EtlPipelines.Agent;

/// <summary>Builds the agent's generic host that <c>Program.cs</c> runs.</summary>
/// <remarks>
/// Split out from <c>Program.cs</c> the same way <c>EtlPipelines.Server.ServerApplication</c> is,
/// so <see cref="EtlPipelines.Agent.Tests"/> can build it and inspect it without ever calling
/// <c>RunAsync()</c>/<c>StartAsync()</c> - <see cref="AgentRegistration"/> is a hosted service, so
/// it (and the real gRPC connection it opens) never runs merely from <see cref="Build"/> itself.
/// </remarks>
public static class AgentApplication
{
    /// <summary>Builds, but does not run, the host.</summary>
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var agentOptionsSection = builder.Configuration.GetSection(AgentOptions.SectionName);
        builder.Services.Configure<AgentOptions>(agentOptionsSection);

        // AddAgentGrpcClient needs the address now, not lazily through IOptions<AgentOptions> -
        // bound into a throwaway instance so its default (AgentOptions.ServerUrl's own property
        // initializer) is the one and only place that default lives, rather than duplicated here.
        var agentOptions = new AgentOptions();
        agentOptionsSection.Bind(agentOptions);
        builder.Services.AddAgentGrpcClient(agentOptions.ServerUrl);

        builder.Services.AddSingleton<PackageInstaller>();
        builder.Services.AddHostedService<AgentRegistration>();

        // agent-cache grows unboundedly through Phase 7 (one directory per installed package
        // version, never cleaned up) - this is the LRU sweep that keeps it bounded (SERVER.md
        // Phase 8).
        builder.Services.AddHostedService<CacheEvictor>();

        return builder.Build();
    }
}
