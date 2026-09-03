using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Services;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Runs;

/// <summary>
/// Turns <c>ManagementService.ExecutePipeline</c> into a real dispatch: creates the <c>Runs</c>
/// row and pushes an <c>ExecutePipeline</c> work item onto a connected agent's <c>Subscribe</c>
/// stream (SERVER.md Phase 6). Fire-and-forget from this class's own point of view -
/// <c>ExecutePipelineResponse</c> hands the run id back immediately rather than waiting for the
/// run to finish (see that RPC's own proto comment), unlike
/// <see cref="AgentConnectionRegistry.DispatchInstallPackageAsync"/>'s awaited install.
/// </summary>
/// <param name="serverUrl">
/// The address embedded in every dispatched work item's <c>ExecutePipelineWorkItem.ServerUrl</c> -
/// where the launched pipeline process's own <c>EtlPipelines.GrpcClient</c> calls back to. Not
/// necessarily the same address the agent itself connects to for <c>AgentService</c> (see that
/// field's own proto comment), though in this phase's single-endpoint topology it always is.
/// </param>
public sealed class RunDispatcher(ServerDbContext dbContext, AgentConnectionRegistry connections, string serverUrl)
{
    /// <summary>Dispatches <paramref name="pipelineId"/> to a connected agent and returns the new run's id.</summary>
    /// <exception cref="RpcException">
    /// <see cref="StatusCode.NotFound"/> when no installed pipeline has that id;
    /// <see cref="StatusCode.FailedPrecondition"/> (<see cref="ServiceScaffolding.NoAgentsAvailable"/>) when no agent is connected to dispatch to.
    /// </exception>
    public async Task<Guid> DispatchAsync(Guid pipelineId, CancellationToken cancellationToken)
    {
        var pipeline = await dbContext.Pipelines
            .Include(p => p.PackageVersion).ThenInclude(v => v.Package)
            .SingleOrDefaultAsync(p => p.Id == pipelineId, cancellationToken)
            .ConfigureAwait(false);

        if (pipeline is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"No installed pipeline '{pipelineId}' is known to this server."));
        }

        if (!connections.TryGetAnyConnectedAgentId(out var agentId))
        {
            throw ServiceScaffolding.NoAgentsAvailable();
        }

        var run = new Run
        {
            Id = Guid.NewGuid(),
            PipelineId = pipeline.Id,
            AgentId = Guid.Parse(agentId),
            Status = "Dispatched",
            RequestedAt = DateTime.UtcNow,
        };

        dbContext.Runs.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        connections.DispatchExecutePipeline(agentId, new ExecutePipelineWorkItem
        {
            SessionId = run.Id.ToString(),
            PackageId = pipeline.PackageVersion.Package.NugetPackageId,
            PackageVersion = pipeline.PackageVersion.Version,
            PipelineName = pipeline.Name,
            ServerUrl = serverUrl,
        });

        return run.Id;
    }
}
