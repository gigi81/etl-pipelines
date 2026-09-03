using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Secrets;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="ManagementService"/> - the end-user/API surface, see that proto's own
/// comments (<c>protos/v1/management.proto</c>).
/// </summary>
/// <remarks>
/// SERVER.md Phase 4 gave this the four metadata-only RPCs (<see cref="ListInstalledPipelines"/>,
/// <see cref="ListAvailablePackages"/>, <see cref="ListUpdates"/>,
/// <see cref="SetConfigurationEntry"/>), real end to end against <c>Server.Database</c> and the
/// configured feed. Phase 5 added <see cref="InstallPackage"/>, dispatched to a connected agent
/// via <see cref="AgentConnectionRegistry"/> and awaited until that agent's own
/// <c>ReportInstallResult</c> resolves it (or it times out). Phase 6 adds
/// <see cref="ExecutePipeline"/> (dispatched via <see cref="RunDispatcher"/>, returning the new
/// run's id immediately rather than waiting for it to finish - see that RPC's own proto comment)
/// and <see cref="StreamRunProgress"/> (backed by <see cref="RunStatusStore"/>, published to by
/// <see cref="PipelineExecutionServiceImpl"/> as the launched process reports in). Everything else
/// here is still Phase 2's <see cref="ServiceScaffolding.Unimplemented"/> stub -
/// <see cref="UninstallPackage"/>, <see cref="UpdatePackage"/> and <see cref="ListAgents"/> are
/// none of them this phase's concern either.
/// </remarks>
public sealed class ManagementServiceImpl(
    PackageCatalogService catalogService,
    SecretsStore secretsStore,
    AgentConnectionRegistry connections,
    RunDispatcher runDispatcher,
    RunStatusStore statusStore)
    : ManagementService.ManagementServiceBase
{
    // How long InstallPackage waits for the dispatched agent to report back before giving up -
    // generous, since a real `dotnet tool install` can mean a genuine NuGet restore, not just a
    // cached instant.
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public override async Task<ListInstalledPipelinesResponse> ListInstalledPipelines(Empty request, ServerCallContext context)
    {
        var pipelines = await catalogService.ListInstalledPipelinesAsync(context.CancellationToken).ConfigureAwait(false);

        var response = new ListInstalledPipelinesResponse();
        response.Pipelines.AddRange(pipelines.Select(pipeline => new InstalledPipeline
        {
            PipelineId = pipeline.PipelineId.ToString(),
            Name = pipeline.Name,
            PackageId = pipeline.PackageId,
            PackageVersion = pipeline.PackageVersion,
        }));
        return response;
    }

    /// <inheritdoc />
    public override async Task<ListAvailablePackagesResponse> ListAvailablePackages(
        ListAvailablePackagesRequest request, ServerCallContext context)
    {
        var searchTerm = string.IsNullOrEmpty(request.SearchTerm) ? null : request.SearchTerm;
        var packages = await catalogService.ListAvailablePackagesAsync(searchTerm, context.CancellationToken).ConfigureAwait(false);

        var response = new ListAvailablePackagesResponse();
        response.Packages.AddRange(packages.Select(package => new AvailablePackage
        {
            PackageId = package.PackageId,
            Versions = { package.Versions },
        }));
        return response;
    }

    /// <inheritdoc />
    public override async Task<ListUpdatesResponse> ListUpdates(Empty request, ServerCallContext context)
    {
        var updates = await catalogService.ListUpdatesAsync(context.CancellationToken).ConfigureAwait(false);

        var response = new ListUpdatesResponse();
        response.Updates.AddRange(updates.Select(update => new PackageUpdate
        {
            PackageId = update.PackageId,
            InstalledVersion = update.InstalledVersion,
            LatestVersion = update.LatestVersion,
        }));
        return response;
    }

    /// <inheritdoc />
    public override async Task<InstallPackageResponse> InstallPackage(InstallPackageRequest request, ServerCallContext context)
    {
        if (!connections.TryGetAnyConnectedAgentId(out var agentId))
        {
            throw ServiceScaffolding.NoAgentsAvailable();
        }

        var version = await catalogService
            .ResolveVersionAsync(request.PackageId, request.Version, context.CancellationToken)
            .ConfigureAwait(false);

        if (version is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound, $"No installable version of '{request.PackageId}' was found on the configured feed."));
        }

        var feedUrls = await catalogService.GetFeedUrlsAsync(context.CancellationToken).ConfigureAwait(false);

        InstallDispatchResult result;
        try
        {
            result = await connections
                .DispatchInstallPackageAsync(agentId, request.PackageId, version, feedUrls, InstallTimeout, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(
                StatusCode.DeadlineExceeded, $"Agent '{agentId}' did not report an install result within {InstallTimeout}."));
        }

        if (!result.Succeeded)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Install failed on agent '{agentId}': {result.Error}"));
        }

        var response = new InstallPackageResponse
        {
            PackageVersionId = result.PackageVersionId!.Value.ToString(),
            Version = version,
        };
        response.PipelineNames.AddRange(result.PipelineNames);
        return response;
    }

    /// <inheritdoc />
    public override Task<Ack> UninstallPackage(UninstallPackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<UpdatePackageResponse> UpdatePackage(UpdatePackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override async Task<ExecutePipelineResponse> ExecutePipeline(ExecutePipelineRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.PipelineId, out var pipelineId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.PipelineId}' is not a valid pipeline id."));
        }

        var runId = await runDispatcher.DispatchAsync(pipelineId, context.CancellationToken).ConfigureAwait(false);
        return new ExecutePipelineResponse { RunId = runId.ToString() };
    }

    /// <inheritdoc />
    public override async Task StreamRunProgress(
        StreamRunProgressRequest request, IServerStreamWriter<RunProgressEvent> responseStream, ServerCallContext context)
    {
        if (!Guid.TryParse(request.RunId, out var runId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.RunId}' is not a valid run id."));
        }

        var reader = statusStore.Subscribe(runId);

        await foreach (var progressEvent in reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(progressEvent).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task<ListAgentsResponse> ListAgents(Empty request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override async Task<Ack> SetConfigurationEntry(SetConfigurationEntryRequest request, ServerCallContext context)
    {
        await secretsStore.SetAsync(request.Key, request.Value, context.CancellationToken).ConfigureAwait(false);
        return new Ack();
    }
}
