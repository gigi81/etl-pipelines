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

    private Task HandleWorkItemAsync(string agentId, WorkItem workItem, CancellationToken cancellationToken) =>
        workItem.KindCase switch
        {
            WorkItem.KindOneofCase.InstallPackage => HandleInstallPackageAsync(agentId, workItem.InstallPackage, workItem.WorkItemId, cancellationToken),
            WorkItem.KindOneofCase.ExecutePipeline => HandleExecutePipelineAsync(agentId, workItem.ExecutePipeline, cancellationToken),
            _ => LogUnhandledAsync(workItem),
        };

    private Task LogUnhandledAsync(WorkItem workItem)
    {
        logger.LogWarning(
            "Ignoring work item {WorkItemId} of kind {Kind} - not handled.", workItem.WorkItemId, workItem.KindCase);
        return Task.CompletedTask;
    }

    private async Task HandleInstallPackageAsync(
        string agentId, InstallPackageWorkItem install, string workItemId, CancellationToken cancellationToken)
    {
        var report = new ReportInstallResultRequest { AgentId = agentId, WorkItemId = workItemId };

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

    /// <summary>
    /// Runs an already-installed pipeline package's shim for <paramref name="work"/> - the run
    /// itself reports its own configuration pull and progress straight to
    /// <c>PipelineExecutionService</c> (via the launched process's own <c>EtlPipelines.GrpcClient</c>,
    /// SERVER.md Phase 6), so this only ever tells the server when the process started and when it
    /// exited, via <c>ReportExecutionStatus</c> - a fallback signal for a run whose process never
    /// managed to report anything itself.
    /// </summary>
    private async Task HandleExecutePipelineAsync(string agentId, ExecutePipelineWorkItem work, CancellationToken cancellationToken)
    {
        try
        {
            var shimPath = packageInstaller.GetInstalledShimPath(work.PackageId, work.PackageVersion);

            await client.ReportExecutionStatusAsync(
                new ReportExecutionStatusRequest
                {
                    AgentId = agentId,
                    SessionId = work.SessionId,
                    Status = ReportExecutionStatusRequest.Types.Status.Started,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var exitCode = await PipelineProcessRunner
                .RunAsync(shimPath, work.PipelineName, work.SessionId, work.ServerUrl, cancellationToken)
                .ConfigureAwait(false);

            await client.ReportExecutionStatusAsync(
                new ReportExecutionStatusRequest
                {
                    AgentId = agentId,
                    SessionId = work.SessionId,
                    Status = ReportExecutionStatusRequest.Types.Status.Exited,
                    ExitCode = exitCode,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately broad, same reasoning as HandleInstallPackageAsync's own catch: this
            // work item's own failure (the package isn't in this agent's cache, the shim could
            // not be started) never takes down the agent's ability to serve other work. Reported
            // as an EXITED(-1) rather than left silent, so the run does not sit "Dispatched"
            // forever with nobody ever having told the server it will never report in.
            logger.LogWarning(
                exception, "Execution failed for pipeline {PipelineName} ({PackageId} {Version})",
                work.PipelineName, work.PackageId, work.PackageVersion);

            try
            {
                await client.ReportExecutionStatusAsync(
                    new ReportExecutionStatusRequest
                    {
                        AgentId = agentId,
                        SessionId = work.SessionId,
                        Status = ReportExecutionStatusRequest.Types.Status.Exited,
                        ExitCode = -1,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reportException) when (reportException is not OperationCanceledException)
            {
                logger.LogWarning(reportException, "Failed to report execution failure for session {SessionId}", work.SessionId);
            }
        }
    }
}
