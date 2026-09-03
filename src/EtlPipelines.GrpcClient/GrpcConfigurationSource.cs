using EtlPipelines.PipelineExecution.V1;
using Microsoft.Extensions.Configuration;

namespace EtlPipelines.GrpcClient;

/// <summary>
/// Pulls this run's configuration from <c>PipelineExecutionService.GetConfiguration</c> once, at
/// startup - the flat, colon-path key/value map <c>ConfigurationEntries</c> (SERVER.md Phase 3)
/// already stores, materialized straight into <see cref="IConfiguration"/> with zero per-connector
/// translation. <c>EtlPipelines.Extensions.Sql.ConnectionRegistration</c>/
/// <c>EtlPipelines.Extensions.Files.Sftp.SftpConnectionRegistration</c> read
/// <c>IConfiguration.GetConnectionString(name)</c>/<c>Sftp:{name}</c> lazily on first connect
/// either way, so this source is a drop-in - neither connector changes.
/// </summary>
/// <remarks>
/// Takes an already-built <see cref="PipelineExecutionService.PipelineExecutionServiceClient"/>
/// rather than a server url - <c>EtlPipelines.Hosting.EtlPipelinesHost</c> (the one built-in
/// caller) builds the channel/client itself (this runs during <c>IHostBuilder.Build()</c>'s
/// app-configuration phase, before the container that would otherwise hand one over even
/// exists), and taking the client directly rather than a url is what lets
/// <see cref="GrpcConfigurationProvider"/> be tested against a mocked client instead of a real
/// socket.
/// </remarks>
public sealed class GrpcConfigurationSource(PipelineExecutionService.PipelineExecutionServiceClient client, string sessionId) : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new GrpcConfigurationProvider(client, sessionId);
}

/// <summary>The <see cref="ConfigurationProvider"/> <see cref="GrpcConfigurationSource"/> builds.</summary>
public sealed class GrpcConfigurationProvider(PipelineExecutionService.PipelineExecutionServiceClient client, string sessionId) : ConfigurationProvider
{
    // A gRPC call with no deadline can hang indefinitely on a stalled connection (a server that
    // never answers, a proxy that swallows the request) - this is the very first thing a run
    // does, so a hang here would otherwise hang the whole process before it ever gets the chance
    // to report anything back, with no failure anyone could see. Failing loudly beats that.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public override void Load()
    {
        var response = client.GetConfiguration(
            new GetConfigurationRequest { SessionId = sessionId }, deadline: DateTime.UtcNow.Add(Timeout));

        foreach (var entry in response.Entries)
        {
            Data[entry.Key] = entry.Value;
        }
    }
}
