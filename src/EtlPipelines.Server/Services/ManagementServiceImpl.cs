using EtlPipelines.Management.V1;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Secrets;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="ManagementService"/> - the end-user/API surface, see that proto's own
/// comments (<c>protos/v1/management.proto</c>).
/// </summary>
/// <remarks>
/// SERVER.md Phase 4: the four metadata-only RPCs (<see cref="ListInstalledPipelines"/>,
/// <see cref="ListAvailablePackages"/>, <see cref="ListUpdates"/>,
/// <see cref="SetConfigurationEntry"/>) are real, working end to end against
/// <c>Server.Database</c> and the configured feed. <see cref="InstallPackage"/> and
/// <see cref="ExecutePipeline"/> are fully implemented too, in the sense that there is nothing
/// left to build in this phase - both fail with
/// <see cref="ServiceScaffolding.NoAgentsAvailable"/> because delegating to an agent is Phase 5's
/// job, not because either method is unfinished. Everything else here is still Phase 2's
/// <see cref="ServiceScaffolding.Unimplemented"/> stub - <see cref="UninstallPackage"/>,
/// <see cref="UpdatePackage"/>, <see cref="StreamRunProgress"/> and <see cref="ListAgents"/> are
/// none of them this phase's concern.
/// </remarks>
public sealed class ManagementServiceImpl(PackageCatalogService catalogService, SecretsStore secretsStore)
    : ManagementService.ManagementServiceBase
{
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
    public override Task<InstallPackageResponse> InstallPackage(InstallPackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.NoAgentsAvailable();

    /// <inheritdoc />
    public override Task<Ack> UninstallPackage(UninstallPackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<UpdatePackageResponse> UpdatePackage(UpdatePackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<ExecutePipelineResponse> ExecutePipeline(ExecutePipelineRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.NoAgentsAvailable();

    /// <inheritdoc />
    public override Task StreamRunProgress(
        StreamRunProgressRequest request, IServerStreamWriter<RunProgressEvent> responseStream, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

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
