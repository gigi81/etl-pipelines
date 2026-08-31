using EtlPipelines.Server.Services;

namespace EtlPipelines.Server;

/// <summary>Builds the gRPC host that <c>Program.cs</c> runs.</summary>
/// <remarks>
/// Split out from <c>Program.cs</c> deliberately: <see cref="EtlPipelines.Server.Tests"/> calls
/// <see cref="Build"/> directly to build the exact same <see cref="WebApplication"/> a real run
/// would - all the way through DI - without ever calling <c>Run()</c>/<c>RunAsync()</c>, so the
/// test never opens a real socket. That is what keeps the smoke test honest with Phase 2's own
/// "no network code yet": <c>WebApplication.CreateBuilder(...).Build()</c> wires everything up but
/// does not start Kestrel listening - only <c>RunAsync()</c> does that, and only <c>Program.cs</c>
/// calls it.
/// </remarks>
public static class ServerApplication
{
    /// <summary>Builds, but does not run, the host.</summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddGrpc();

        var app = builder.Build();

        app.MapGrpcService<PipelineExecutionServiceImpl>();
        app.MapGrpcService<AgentServiceImpl>();
        app.MapGrpcService<ManagementServiceImpl>();

        // gRPC clients don't hit this, but a bare `curl` against the port otherwise gets an
        // unhelpful 404 - the same courtesy the official grpc-dotnet templates extend.
        app.MapGet("/", () =>
            "EtlPipelines.Server hosts gRPC services only - use a gRPC client to reach them.");

        return app;
    }
}
