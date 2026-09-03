using EtlPipelines.AgentExecution.V1;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent;

/// <summary>
/// Registers with the server once at startup, then holds <c>AgentService.Subscribe</c> open for
/// this agent's whole lifetime, installing whatever <c>InstallPackage</c> work items arrive and
/// reporting the result back - closing the install loop (SERVER.md Phase 5). Sends a heartbeat on
/// its own timer alongside the <c>Subscribe</c> loop, on the same registered identity.
/// </summary>
public sealed class AgentRegistration(
    AgentService.AgentServiceClient client,
    PackageInstaller packageInstaller,
    IOptions<AgentOptions> options,
    ILogger<AgentRegistration> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registerRequest = new RegisterAgentRequest
        {
            MachineName = Environment.MachineName,
            Version = typeof(AgentRegistration).Assembly.GetName().Version?.ToString() ?? "0.0.0",
        };
        registerRequest.Tags.AddRange(options.Value.Tags);

        var registerResponse = await client
            .RegisterAgentAsync(registerRequest, cancellationToken: stoppingToken)
            .ConfigureAwait(false);
        var agentId = registerResponse.AgentId;

        logger.LogInformation("Registered as agent {AgentId}", agentId);

        var heartbeatTask = SendHeartbeatsAsync(agentId, stoppingToken);
        await SubscribeAsync(agentId, stoppingToken).ConfigureAwait(false);
        await heartbeatTask.ConfigureAwait(false);
    }

    private async Task SendHeartbeatsAsync(string agentId, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await client
                    .HeartbeatAsync(new AgentHeartbeatRequest { AgentId = agentId }, cancellationToken: stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown - the agent is stopping, not a failure to report.
        }
    }

    private async Task SubscribeAsync(string agentId, CancellationToken stoppingToken)
    {
        using var call = client.Subscribe(new SubscribeRequest { AgentId = agentId }, cancellationToken: stoppingToken);

        try
        {
            await foreach (var workItem in call.ResponseStream.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await HandleWorkItemAsync(agentId, workItem, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown.
        }
    }

    private async Task HandleWorkItemAsync(string agentId, WorkItem workItem, CancellationToken cancellationToken)
    {
        if (workItem.KindCase != WorkItem.KindOneofCase.InstallPackage)
        {
            // ExecutePipeline work items are Phase 6's concern - nothing dispatches one yet, but
            // this guards against silently doing nothing if one ever arrives before that phase
            // lands, rather than failing in a confusing way somewhere downstream.
            logger.LogWarning(
                "Ignoring work item {WorkItemId} of kind {Kind} - not handled until a later phase.",
                workItem.WorkItemId, workItem.KindCase);
            return;
        }

        var install = workItem.InstallPackage;
        var report = new ReportInstallResultRequest { AgentId = agentId, WorkItemId = workItem.WorkItemId };

        try
        {
            var shimPath = await packageInstaller
                .InstallAsync(install.PackageId, install.Version, install.FeedUrls, cancellationToken)
                .ConfigureAwait(false);
            var pipelineNames = await PipelineProcessRunner
                .ListPipelineNamesAsync(shimPath, cancellationToken)
                .ConfigureAwait(false);

            report.Succeeded = true;
            report.PipelineNames.AddRange(pipelineNames);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately broad: whatever went wrong installing or listing this one package
            // becomes a reported failure for this one work item, not a crashed agent that can no
            // longer serve any other work.
            logger.LogWarning(exception, "Install failed for {PackageId} {Version}", install.PackageId, install.Version);
            report.Succeeded = false;
            report.Error = exception.Message;
        }

        await client.ReportInstallResultAsync(report, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
