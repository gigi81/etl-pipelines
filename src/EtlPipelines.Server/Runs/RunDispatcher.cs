using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live;
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
public sealed class RunDispatcher(
    ServerDbContext dbContext, AgentConnectionRegistry connections, LiveState liveState, string serverUrl)
{
    /// <summary>
    /// Dispatches <paramref name="pipelineId"/> to a connected agent and returns the new run's id.
    /// An agent known to have the pipeline's package version installed is preferred; any other
    /// connected agent installs it on demand from the feed URLs the work item carries.
    /// </summary>
    /// <exception cref="RpcException">
    /// <see cref="StatusCode.NotFound"/> when no installed pipeline has that id;
    /// <see cref="StatusCode.FailedPrecondition"/> when its package version was uninstalled or
    /// superseded by <c>UpdatePackage</c>, or (<see cref="ServiceScaffolding.NoAgentsAvailable"/>)
    /// when no agent is connected to dispatch to.
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

        if (pipeline.PackageVersion.Status != PackageVersionStatus.Installed)
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Pipeline '{pipelineId}' belongs to {pipeline.PackageVersion.Package.NugetPackageId} " +
                $"{pipeline.PackageVersion.Version}, which is {pipeline.PackageVersion.Status}, not {PackageVersionStatus.Installed}."));
        }

        var packageId = pipeline.PackageVersion.Package.NugetPackageId;
        var version = pipeline.PackageVersion.Version;

        if (!connections.TryGetConnectedAgentIdFor(packageId, version, out var agentId))
        {
            throw ServiceScaffolding.NoAgentsAvailable();
        }

        var feedUrls = await dbContext.NuGetFeeds
            .AsNoTracking()
            .OrderBy(feed => feed.Ordinal)
            .Select(feed => feed.Url)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

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

        // Before the agent can hear about it, so whatever it reports back finds the run already there.
        await liveState.RunCreatedAsync(dbContext, run.Id, cancellationToken).ConfigureAwait(false);

        var workItem = new ExecutePipelineWorkItem
        {
            SessionId = run.Id.ToString(),
            PackageId = packageId,
            PackageVersion = version,
            PipelineName = pipeline.Name,
            ServerUrl = serverUrl,
        };
        workItem.FeedUrls.AddRange(feedUrls);

        connections.DispatchExecutePipeline(agentId, workItem);

        // Either it already had it, or it installs it before running.
        connections.MarkInstalled(agentId, packageId, version);

        return run.Id;
    }
}
