using Microsoft.Extensions.Hosting;

namespace EtlPipelines.Agent;

/// <summary>Builds the agent's generic host that <c>Program.cs</c> runs.</summary>
/// <remarks>
/// Split out from <c>Program.cs</c> the same way <c>EtlPipelines.Server.ServerApplication</c> is,
/// so <see cref="EtlPipelines.Agent.Tests"/> can build it and inspect it without ever calling
/// <c>RunAsync()</c>.
/// </remarks>
public static class AgentApplication
{
    /// <summary>Builds, but does not run, the host.</summary>
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Phase 5 registers AgentRegistration (the BackgroundService that holds
        // AgentService.Subscribe open and receives work items), PackageInstaller,
        // PipelineProcessRunner and ResourceMonitor here. Phase 2 only stands the host up so it
        // builds clean under the same TreatWarningsAsErrors=true as everything else - no network
        // code yet.

        return builder.Build();
    }
}
