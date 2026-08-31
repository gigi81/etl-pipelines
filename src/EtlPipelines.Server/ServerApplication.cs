using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Secrets;
using EtlPipelines.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server;

/// <summary>Builds the gRPC host that <c>Program.cs</c> runs.</summary>
/// <remarks>
/// Split out from <c>Program.cs</c> deliberately: <see cref="EtlPipelines.Server.Tests"/> calls
/// <see cref="Build"/> directly to build the exact same <see cref="WebApplication"/> a real run
/// would - all the way through DI - without ever calling <c>Run()</c>/<c>RunAsync()</c>, so the
/// test never opens a real socket or a real Postgres connection. That is what keeps the smoke test
/// honest: <c>WebApplication.CreateBuilder(...).Build()</c> wires everything up but does not start
/// Kestrel listening, and <c>AddDbContext</c>/<see cref="NuGetFeedClient"/> are both lazy - neither
/// touches a real connection until something actually queries through them, which the hosted
/// <see cref="NuGetFeedSeeder"/> is the first of, and hosted services only run once
/// <c>RunAsync()</c>/<c>StartAsync()</c> does, never merely from <see cref="Build"/> itself.
/// </remarks>
public static class ServerApplication
{
    /// <summary>Builds, but does not run, the host.</summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddGrpc();

        // Falls back to a syntactically valid but unreachable connection string rather than null/
        // empty when unconfigured - a plain `dotnet run` with no appsettings.Development.json
        // fails clearly the first time something actually queries, instead of failing confusingly
        // at DI-registration time out of UseNpgsql's own null-argument check.
        builder.Services.AddDbContext<ServerDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("Server")
                ?? "Host=localhost;Database=etlpipelines_server;Username=postgres;Password=postgres"));

        // Explicitly not production-grade key management - see SERVER.md's "Secrets at rest"
        // decision. KeyRing:Path becomes the server-cache volume in Phase 7; "keys" is a
        // reasonable default for a bare local run.
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["KeyRing:Path"] ?? "keys"));

        builder.Services.AddSingleton<INuGetFeedClient, NuGetFeedClient>();
        builder.Services.AddScoped<PackageCatalogService>();
        builder.Services.AddScoped<SecretsStore>();
        builder.Services.AddHostedService<NuGetFeedSeeder>();

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
