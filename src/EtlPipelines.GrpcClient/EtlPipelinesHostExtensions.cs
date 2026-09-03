using System.CommandLine;
using EtlPipelines.Hosting;
using EtlPipelines.PipelineExecution.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.GrpcClient;

/// <summary>
/// Wires <c>--session-id</c>/<c>--server-url</c> into an <see cref="EtlPipelinesHost"/> - built
/// exactly like <see cref="EtlPipelinesHost"/> already builds <see cref="EtlPipelinesHost.VerboseOption"/>/
/// <see cref="EtlPipelinesHost.WorkDirOption"/> itself: declared as <c>static Option&lt;T&gt;</c>
/// properties, added to the root command as <c>Recursive = true</c>, read off the
/// <see cref="ParseResult"/> while services are still being composed (SERVER.md Phase 6).
/// </summary>
/// <remarks>
/// A pipeline application opts in with one line:
/// <code>
/// return new EtlPipelinesHost("sales")
///     .UseGrpcClient()
///     .ConfigureServices((_, services, workspace) =&gt; ...)
///     .RunAsync(args);
/// </code>
/// Both options are optional - a run with neither behaves exactly as it did before this was ever
/// called (every sample already works this way; <see cref="UseGrpcClient"/> must not break that),
/// which is what lets the same shim run standalone during development and under an
/// <c>EtlPipelines.Agent</c> dispatch unmodified.
/// </remarks>
public static class EtlPipelinesHostExtensions
{
    /// <summary>The run-scoped session id the server issued for this run - mirrors <c>Runs.Id</c> (SERVER.md Phase 3).</summary>
    public static Option<string?> SessionIdOption { get; } = new("--session-id")
    {
        Description = "The run-scoped session id issued by the server for this run. Together with " +
            "--server-url, turns on gRPC-backed configuration pull and progress reporting; omit both for a plain local run.",
        Recursive = true,
    };

    /// <summary>The <c>EtlPipelines.Server</c> gRPC endpoint this run reports progress to and pulls configuration from.</summary>
    public static Option<string?> ServerUrlOption { get; } = new("--server-url")
    {
        Description = "The EtlPipelines.Server gRPC endpoint this run reports progress to and pulls configuration from.",
        Recursive = true,
    };

    /// <summary>
    /// Adds <see cref="SessionIdOption"/>/<see cref="ServerUrlOption"/> and, when both are given on
    /// the command line, pulls configuration from and reports progress to the named server for the
    /// whole run.
    /// </summary>
    public static EtlPipelinesHost UseGrpcClient(this EtlPipelinesHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        host.AddRecursiveOption(SessionIdOption);
        host.AddRecursiveOption(ServerUrlOption);

        host.ConfigureServices((result, services) =>
        {
            var (sessionId, serverUrl) = SessionOf(result);
            if (sessionId is null || serverUrl is null)
            {
                return;
            }

            // Cleartext HTTP/2 (h2c) - see EtlPipelines.Agent.GrpcClient.AddAgentGrpcClient's own
            // copy of this switch for why.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            services.AddGrpcClient<PipelineExecutionService.PipelineExecutionServiceClient>(
                options => options.Address = new Uri(serverUrl));

            services.AddSingleton(provider => new GrpcProgressReporter(
                provider.GetRequiredService<PipelineExecutionService.PipelineExecutionServiceClient>(),
                sessionId,
                provider.GetRequiredService<ILogger<GrpcProgressReporter>>()));
        });

        // The no-ParseResult overload, deliberately: it just registers the nested
        // ConfigureAppConfiguration callback with the still-being-built IHostBuilder, which is
        // always safe to do regardless of whether Parse(args) has run yet. host.ParseResult
        // itself is only actually read once that nested callback runs - during Build()'s
        // app-configuration phase, well after RunAsync's own Parse(args) call, even though
        // UseGrpcClient() itself (and so this whole method) runs before RunAsync is ever called.
        host.ConfigureHost(hostBuilder => hostBuilder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var (sessionId, serverUrl) = SessionOf(host.ParseResult);
            if (sessionId is null || serverUrl is null)
            {
                return;
            }

            // Cleartext HTTP/2 (h2c) - see EtlPipelines.Agent.GrpcClient.AddAgentGrpcClient's own
            // copy of this switch for why.
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            // A short-lived channel/client of its own, not the one AddGrpcClient registers below
            // - this runs before the container that would otherwise hand one over even exists.
            var channel = GrpcChannel.ForAddress(serverUrl);
            var client = new PipelineExecutionService.PipelineExecutionServiceClient(channel);
            configBuilder.Add(new GrpcConfigurationSource(client, sessionId));
        }));

        // GetService, not GetRequiredService: GrpcProgressReporter is only registered above when
        // both options were actually given, and resolving it here is what starts it listening -
        // the same eager-resolve contract EtlPipelinesHost's own constructor already gives
        // PipelineTraceLogger.
        return host.ConfigureApplication(services => services.GetService<GrpcProgressReporter>());
    }

    private static (string? SessionId, string? ServerUrl) SessionOf(ParseResult result)
    {
        var sessionId = result.GetValue(SessionIdOption);
        var serverUrl = result.GetValue(ServerUrlOption);

        return string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(serverUrl)
            ? (null, null)
            : (sessionId, serverUrl);
    }
}
