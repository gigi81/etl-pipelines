using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Runs;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="AgentService"/> - see that proto's own comments
/// (<c>protos/v1/agent_execution.proto</c>) for who calls this and why it is a separate, broader
/// surface than <see cref="PipelineExecutionServiceImpl"/>.
/// </summary>
/// <remarks>
/// SERVER.md Phase 5: registration (<see cref="RegisterAgent"/>), liveness
/// (<see cref="Heartbeat"/>), and the work-item dispatch loop
/// (<see cref="Subscribe"/>/<see cref="ReportInstallResult"/>, via
/// <see cref="AgentConnectionRegistry"/>) are real. Phase 6 adds <see cref="ReportExecutionStatus"/>
/// itself, now that <c>RunDispatcher</c> actually sends an agent an <c>ExecutePipeline</c> work
/// item to run - a fallback signal only: the launched process's own
/// <c>PipelineExecutionService.ReportRunResult</c> is what normally settles a run's final state,
/// so this only ever fills in <c>Runs</c> when the process exited without managing to report that
/// itself.
/// </remarks>
public sealed class AgentServiceImpl(
    ServerDbContext dbContext, AgentConnectionRegistry connections, PackageCatalogService catalogService, RunStatusStore statusStore)
    : AgentService.AgentServiceBase
{
    /// <inheritdoc />
    public override async Task<RegisterAgentResponse> RegisterAgent(RegisterAgentRequest request, ServerCallContext context)
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            MachineName = request.MachineName,
            Tags = request.Tags.ToList(),
            Version = request.Version,
            Status = "Online",
            LastHeartbeatAt = DateTime.UtcNow,
        };

        dbContext.Agents.Add(agent);
        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        return new RegisterAgentResponse { AgentId = agent.Id.ToString() };
    }

    /// <inheritdoc />
    public override async Task<AgentExecution.V1.Ack> Heartbeat(AgentHeartbeatRequest request, ServerCallContext context)
    {
        // A heartbeat from an agent id Server.Database has no row for (a restart wiped the
        // in-memory registry, but not the agent process itself, which keeps heartbeating its old
        // id) is silently ignored rather than an error - Phase 8 is what actually decides what
        // "stale agent identity" should do; for now, nothing to update is not a failure.
        var agentId = Guid.Parse(request.AgentId);
        var agent = await dbContext.Agents
            .SingleOrDefaultAsync(a => a.Id == agentId, context.CancellationToken)
            .ConfigureAwait(false);

        if (agent is not null)
        {
            agent.LastHeartbeatAt = DateTime.UtcNow;
            agent.Status = "Online";
            await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        }

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

        var run = await dbContext.Runs
            .SingleOrDefaultAsync(r => r.Id == runId, context.CancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return new AgentExecution.V1.Ack();
        }

        switch (request.Status)
        {
            case ReportExecutionStatusRequest.Types.Status.Started:
                run.StartedAt ??= DateTime.UtcNow;
                if (run.Status is "Queued" or "Dispatched")
                {
                    run.Status = "Running";
                }

                break;

            case ReportExecutionStatusRequest.Types.Status.Exited:
                // The launched process's own ReportRunResult (PipelineExecutionServiceImpl) is
                // what normally settles Status/ExitCode/row counts - only fill them in here when
                // the process exited without ever managing to report that itself (crashed before
                // it could, or the server never heard from it), so a run is never left "Running"
                // forever once the agent already knows it is done. Left untouched if
                // ReportRunResult already settled it first - which also means it already
                // published its own RunCompleted, so this never publishes a second one for the
                // same run.
                if (run.Status is "Queued" or "Dispatched" or "Running")
                {
                    var succeeded = request.ExitCode == 0;
                    run.Status = succeeded ? "Succeeded" : "Failed";
                    run.CompletedAt ??= DateTime.UtcNow;
                    run.ExitCode = request.ExitCode;

                    // Without this, StreamRunProgress would wait forever for a RunCompleted event
                    // that ReportRunResult was supposed to publish but never got the chance to -
                    // this fallback is the only other place a run's terminal state is ever known,
                    // so it has to close the stream out the same way.
                    statusStore.PublishRunCompleted(run.Id, new RunCompleted
                    {
                        Status = succeeded ? RunCompleted.Types.Status.Succeeded : RunCompleted.Types.Status.Failed,
                        ExitCode = request.ExitCode,
                    });
                }

                break;

            case ReportExecutionStatusRequest.Types.Status.ResourceSample:
                dbContext.AgentResourceSamples.Add(new AgentResourceSample
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    AgentId = Guid.Parse(request.AgentId),
                    SampledAt = DateTime.UtcNow,
                    CpuPercent = request.CpuPercent,
                    WorkingSetBytes = request.WorkingSetBytes,
                });
                break;
        }

        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        return new AgentExecution.V1.Ack();
    }
}
