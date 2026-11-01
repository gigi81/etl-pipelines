using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Live;
using Google.Protobuf;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="AgentService"/> - see that proto's own comments
/// (<c>protos/v1/agent_execution.proto</c>) for who calls this and why it is a separate, broader
/// surface than <see cref="PipelineExecutionServiceImpl"/>.
/// </summary>
/// <remarks>
/// Registration (<see cref="RegisterAgent"/>), liveness (<see cref="Heartbeat"/>) and what an agent
/// reports about a run (<see cref="ReportExecutionStatus"/>) go into <see cref="LiveState"/>, which
/// <see cref="LiveStateSync"/> writes to the database a few seconds later. The work-item dispatch
/// loop (<see cref="Subscribe"/>/<see cref="ReportInstallResult"/>, via
/// <see cref="AgentConnectionRegistry"/>) is connection state, not stored. <see cref="ReportExecutionStatus"/>
/// is a fallback signal: the launched process's own
/// <c>PipelineExecutionService.ReportRunResult</c> is what normally settles a run's final state, so
/// it only ever ends a run when the process exited without managing to report that itself.
/// </remarks>
public sealed class AgentServiceImpl(
    AgentConnectionRegistry connections,
    PackageCatalogService catalogService,
    LiveState liveState)
    : AgentService.AgentServiceBase
{
    /// <inheritdoc />
    public override async Task<RegisterAgentResponse> RegisterAgent(RegisterAgentRequest request, ServerCallContext context)
    {
        // An agent that has registered before brings the id it was given, and is then the same agent.
        Guid? requestedId = null;
        if (request.AgentId.Length > 0)
        {
            requestedId = Guid.TryParse(request.AgentId, out var parsed) && parsed != Guid.Empty
                ? parsed
                : throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.AgentId}' is not a valid agent id."));
        }

        var agentId = await liveState.RegisterAgentAsync(
            requestedId,
            request.MachineName,
            request.Tags.ToList(),
            request.Version,
            request.HostInfo is { Fields.Count: > 0 } info ? JsonFormatter.Default.Format(info) : null,
            context.CancellationToken).ConfigureAwait(false);

        return new RegisterAgentResponse { AgentId = agentId.ToString() };
    }

    /// <inheritdoc />
    public override async Task<AgentExecution.V1.Ack> Heartbeat(AgentHeartbeatRequest request, ServerCallContext context)
    {
        // A heartbeat from an agent id the server does not know (it was never registered here, or the
        // server lost it) is silently ignored rather than an error - nothing to update is not a failure.
        await liveState.AgentHeartbeatAsync(Guid.Parse(request.AgentId), context.CancellationToken).ConfigureAwait(false);
        return new AgentExecution.V1.Ack();
    }

    /// <inheritdoc />
    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<WorkItem> responseStream, ServerCallContext context)
    {
        // Unbounded: a slow-to-drain agent should never make ManagementService.InstallPackage's
        // dispatch fail to enqueue - it only ever fails by timing out waiting for
        // ReportInstallResult, which is the more meaningful failure to surface.
        var channel = Channel.CreateUnbounded<WorkItem>();
        connections.Connect(request.AgentId, channel);

        try
        {
            await foreach (var workItem in channel.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(workItem).ConfigureAwait(false);
            }
        }
        finally
        {
            // Reached on ordinary cancellation (the agent disconnected) as much as on a real
            // fault - either way, this agent can no longer be dispatched to.
            connections.Disconnect(request.AgentId);
        }
    }

    /// <inheritdoc />
    public override async Task<AgentExecution.V1.Ack> ReportInstallResult(ReportInstallResultRequest request, ServerCallContext context)
    {
        Guid? packageVersionId = null;

        if (connections.TryGetPendingInstall(request.WorkItemId, out var pending))
        {
            packageVersionId = await catalogService.RecordInstallResultAsync(
                pending.PackageId,
                pending.Version,
                request.Succeeded,
                request.PipelineNames,
                context.CancellationToken).ConfigureAwait(false);

            if (request.Succeeded)
            {
                connections.MarkInstalled(request.AgentId, pending.PackageId, pending.Version);

                // New pipelines may have appeared - tell whoever is showing the list.
                liveState.PipelinesChanged();
            }
        }

        connections.TryCompleteInstall(
            request.WorkItemId,
            new InstallDispatchResult(
                request.Succeeded,
                packageVersionId,
                request.PipelineNames.ToList(),
                request.Succeeded ? null : request.Error));

        return new AgentExecution.V1.Ack();
    }

    /// <inheritdoc />
    public override async Task<AgentExecution.V1.Ack> ReportExecutionStatus(ReportExecutionStatusRequest request, ServerCallContext context)
    {
        // Malformed or unknown - nothing meaningful to record. Reported statuses are operationally
        // low-stakes (the process's own ReportRunResult is authoritative), so this stays a no-op
        // Ack rather than an error a well-behaved agent would have to handle.
        if (!Guid.TryParse(request.SessionId, out var runId))
        {
            return new AgentExecution.V1.Ack();
        }

        switch (request.Status)
        {
            case ReportExecutionStatusRequest.Types.Status.Started:
                await liveState.StartRunAsync(runId, context.CancellationToken).ConfigureAwait(false);
                break;

            case ReportExecutionStatusRequest.Types.Status.Exited:
                // The launched process's own ReportRunResult is what normally settles the run - this
                // only ends it if the process exited without ever managing to report that itself
                // (crashed before it could, or the server never heard from it), so a run is never left
                // running forever once the agent already knows it is done. A run that already
                // reported is left as it is.
                await liveState.ProcessExitedAsync(runId, request.ExitCode, context.CancellationToken).ConfigureAwait(false);
                break;

            case ReportExecutionStatusRequest.Types.Status.ResourceSample:
                await liveState.ResourceSampledAsync(
                    runId, Guid.Parse(request.AgentId), request.CpuPercent, request.WorkingSetBytes, context.CancellationToken).ConfigureAwait(false);
                break;
        }

        return new AgentExecution.V1.Ack();
    }
}
