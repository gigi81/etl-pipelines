using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Secrets;
using EtlPipelines.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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

        // gRPC needs HTTP/2, and Kestrel's own default for a plain "http://" endpoint (no TLS) is
        // HTTP/1.1 only - ALPN would negotiate HTTP/2 automatically over HTTPS, but there is no
        // TLS anywhere in this phase (see SERVER.md's "Auth/authz: deferred entirely"), so every
        // endpoint is explicitly opted into cleartext HTTP/2 (h2c) here instead. Agent.GrpcClient's
        // AddAgentGrpcClient sets the matching client-side switch.
        //
        // Two things had to be caught the hard way, with a real `curl --http2-prior-knowledge`
        // against a real running instance, before this endpoint actually spoke h2c:
        //
        // 1. ConfigureEndpointDefaults alone is not enough. Endpoints derived purely from --urls/
        //    ASPNETCORE_URLS (or from Kestrel's own completely-unconfigured "http://localhost:5000"
        //    fallback) go through a legacy binding path that never applies EndpointDefaults at all,
        //    regardless of what ConfigureEndpointDefaults says - only an endpoint Kestrel is
        //    explicitly told about (Listen(...)/ListenAnyIP(...), or the Kestrel:Endpoints config
        //    section) goes through the code path that actually honors it. Server:Port (default
        //    5000, matching what --urls used to default to; EtlPipelines.Server.Tests overrides it
        //    to 0 for an OS-assigned ephemeral port in InstallLoopDockerTests) plus ListenAnyIP(...)
        //    is what makes this a real, explicit endpoint.
        // 2. Even as a real, explicit endpoint, HttpProtocols.Http1AndHttp2 (mixed) silently
        //    degrades to HTTP/1.1-only without TLS - Kestrel logs exactly that ("HTTP/2 requires
        //    TLS application protocol negotiation... Connections to this endpoint will use
        //    HTTP/1.1") rather than failing loudly. Without TLS there is no ALPN to pick a protocol
        //    per connection, and Kestrel does not sniff a mixed cleartext endpoint - a cleartext
        //    endpoint has to dedicate itself to one protocol. HttpProtocols.Http2 alone is that
        //    dedication; this endpoint serves gRPC exclusively (the friendly "/" route below is
        //    reachable over h2c same as everything else, just not over a plain HTTP/1.1 curl -
        //    already the "gRPC clients don't hit this" courtesy that route's own comment names).
        var port = builder.Configuration.GetValue("Server:Port", 5000);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
            options.ListenAnyIP(port, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        });

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

        // Process-wide connection/dispatch state (Phase 5) - a singleton, not scoped, since a
        // connected agent's Subscribe stream and a pending InstallPackage dispatch both outlive
        // any one gRPC call.
        builder.Services.AddSingleton<AgentConnectionRegistry>();

        // Process-wide run-progress fan-out (Phase 6) - a singleton for the same reason as
        // AgentConnectionRegistry above: a run's history and its live StreamRunProgress
        // subscribers outlive any one gRPC call.
        builder.Services.AddSingleton<RunStatusStore>();

        // Real wall-clock time in production; AgentLivenessMonitorTests substitutes a fake one
        // directly (it does not go through DI) to drive the timeout decision without a real
        // elapsed wait.
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<AgentLivenessOptions>(builder.Configuration.GetSection(AgentLivenessOptions.SectionName));

        // An agent that has stopped heartbeating (its process died, rather than a clean
        // Subscribe disconnect) is otherwise invisible to every request-driven code path in this
        // file - this is the one background sweep that actually notices (SERVER.md Phase 8).
        builder.Services.AddHostedService<AgentLivenessMonitor>();

        // The address embedded in every ExecutePipeline work item's ServerUrl - where a launched
        // pipeline process's own EtlPipelines.GrpcClient calls back to. Server:PublicUrl is what
        // an operator sets when this server is reachable at a different address than the one it
        // binds to (a reverse proxy, a container's published port, or another container entirely
        // in docker-compose, which needs the "server" hostname rather than any address this
        // process could discover about itself). Resolved lazily, from IServer's own real bound
        // address when unconfigured, rather than guessed at Build() time (before Kestrel has
        // actually bound anything) - RunDispatcher is only ever resolved once ExecutePipeline
        // itself is being handled, which can only happen after StartAsync() has bound Kestrel for
        // real, so this is never called too early.
        builder.Services.AddScoped(provider => new RunDispatcher(
            provider.GetRequiredService<ServerDbContext>(),
            provider.GetRequiredService<AgentConnectionRegistry>(),
            ResolvePublicUrl(provider)));

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

    /// <summary>
    /// <c>Server:PublicUrl</c> if an operator set one; otherwise Kestrel's own real bound address
    /// (the same "http://[::]:port" -&gt; "http://127.0.0.1:port" rewrite
    /// <c>EtlPipelines.Server.Tests</c>' own end-to-end tests already have to do for their client
    /// to dial the same instance), never a value assembled from configuration alone before Kestrel
    /// has actually bound anything - a port passed in ahead of time (0 for an OS-assigned one, or
    /// one reserved separately) is not guaranteed to be the one Kestrel actually ends up listening
    /// on, or reachable the same way from a separately-launched process.
    /// </summary>
    private static string ResolvePublicUrl(IServiceProvider provider)
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var configured = configuration["Server:PublicUrl"];
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        var addresses = provider.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var boundAddress = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Server:PublicUrl is not set and Kestrel has not bound any address yet - " +
                "this can only be resolved once the server has actually started.");

        return $"http://127.0.0.1:{new Uri(boundAddress).Port}";
    }
}
