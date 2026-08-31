using EtlPipelines.Management.V1;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="ManagementService"/> - the end-user/API surface, see that proto's own
/// comments (<c>protos/v1/management.proto</c>).
/// </summary>
/// <remarks>
/// Phase 2 scaffolding: every method exists so the full v1 surface is hosted and the solution
/// builds clean, but none has real behaviour yet - <see cref="ServiceScaffolding.Unimplemented"/>
/// is what every one of them returns until Phase 4 (catalog/metadata, against a real
/// <c>Server.Database</c> and <c>bagetter</c>) and Phase 6 (<see cref="ExecutePipeline"/>,
/// <see cref="StreamRunProgress"/>) fill them in.
/// </remarks>
public sealed class ManagementServiceImpl : ManagementService.ManagementServiceBase
{
    /// <inheritdoc />
    public override Task<ListInstalledPipelinesResponse> ListInstalledPipelines(Empty request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<ListAvailablePackagesResponse> ListAvailablePackages(
        ListAvailablePackagesRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<ListUpdatesResponse> ListUpdates(Empty request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<InstallPackageResponse> InstallPackage(InstallPackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> UninstallPackage(UninstallPackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<UpdatePackageResponse> UpdatePackage(UpdatePackageRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<ExecutePipelineResponse> ExecutePipeline(ExecutePipelineRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task StreamRunProgress(
        StreamRunProgressRequest request, IServerStreamWriter<RunProgressEvent> responseStream, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<ListAgentsResponse> ListAgents(Empty request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> SetConfigurationEntry(SetConfigurationEntryRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();
}
