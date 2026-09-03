using EtlPipelines.AgentExecution.V1;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Agent.GrpcClient;

/// <summary>Registers the typed <see cref="AgentService.AgentServiceClient"/> against a server URL.</summary>
public static class AgentGrpcClientExtensions
{
    /// <summary>
    /// Adds <see cref="AgentService.AgentServiceClient"/> to the container, pointed at
    /// <paramref name="serverUrl"/> - the same URL <c>--server-url</c>
    /// (<c>EtlPipelines.Hosting.EtlPipelinesHost.UseGrpcClient</c>, Phase 6) hands a launched
    /// pipeline process, but resolved for the agent itself here at startup rather than read off a
    /// per-run command line.
    /// </summary>
    public static IServiceCollection AddAgentGrpcClient(this IServiceCollection services, string serverUrl)
    {
        // Cleartext HTTP/2 (h2c) support is opt-in per client, not just per server - matches the
        // Kestrel side EtlPipelines.Server.ServerApplication configures, since neither end of this
        // connection uses TLS in this phase (SERVER.md: "Auth/authz: deferred entirely"). Without
        // this, a plain "http://" address here fails outright rather than falling back to
        // HTTP/1.1: gRPC has no HTTP/1.1 fallback.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        services.AddGrpcClient<AgentService.AgentServiceClient>(options => options.Address = new Uri(serverUrl));
        return services;
    }
}
