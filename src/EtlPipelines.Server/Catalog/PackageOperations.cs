using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Services;
using Grpc.Core;

namespace EtlPipelines.Server.Catalog;

/// <summary>
/// Installing, updating and uninstalling packages - the one implementation behind both the gRPC
/// <c>ManagementService</c> and the REST API the WebUI uses. Failures are <see cref="RpcException"/>s
/// with the gRPC status that describes them, which each surface maps onto its own error format.
/// </summary>
/// <remarks>
/// An install is carried out by a connected agent (<c>dotnet tool install</c>): the Server sends it a
/// work item and waits for the agent to report back.
/// </remarks>
public sealed class PackageOperations(PackageCatalogService catalogService, AgentConnectionRegistry connections, LiveState liveState)
{
    // How long an install waits for the dispatched agent to report back before giving up -
    // generous, since a real `dotnet tool install` can mean a genuine NuGet restore, not just a
    // cached instant.
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Installs <paramref name="version"/> of <paramref name="packageId"/> on a connected agent - the
    /// latest stable version the feed reports when it is null or empty.
    /// </summary>
    /// <exception cref="RpcException">
    /// <see cref="StatusCode.NotFound"/> when the feed has no such package;
    /// <see cref="StatusCode.FailedPrecondition"/> when no agent is connected;
    /// <see cref="StatusCode.DeadlineExceeded"/> when the agent does not answer in time;
    /// <see cref="StatusCode.Internal"/> when the agent's install fails.
    /// </exception>
    public async Task<InstalledPackage> InstallAsync(string packageId, string? version, CancellationToken cancellationToken)
    {
        var resolved = await catalogService.ResolveVersionAsync(packageId, version, cancellationToken).ConfigureAwait(false);
        return await InstallOnAnAgentAsync(packageId, resolved, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs the latest stable version of an installed package, then retires every older installed
    /// version, so its pipelines are listed and run at the new version only. Runs already in flight are
    /// not interrupted. Needs no agent when it is already on the latest version.
    /// </summary>
    /// <exception cref="RpcException">As <see cref="InstallAsync"/>, plus <see cref="StatusCode.FailedPrecondition"/> when the package is not installed.</exception>
    public async Task<InstalledPackage> UpdateAsync(string packageId, CancellationToken cancellationToken)
    {
        if (!await catalogService.IsInstalledAsync(packageId, cancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition, $"'{packageId}' is not installed - install it before updating it."));
        }

        var latest = await catalogService.ResolveVersionAsync(packageId, requestedVersion: null, cancellationToken).ConfigureAwait(false);

        // Already on the latest version: nothing to send to an agent, so this works with none
        // connected - only retiring the older versions below still applies.
        var current = latest is null
            ? null
            : await catalogService.FindInstalledVersionAsync(packageId, latest, cancellationToken).ConfigureAwait(false);

        var installed = current is not null
            ? new InstalledPackage(current.PackageVersionId, latest!, current.PipelineNames)
            : await InstallOnAnAgentAsync(packageId, latest, cancellationToken).ConfigureAwait(false);

        await catalogService.SupersedeOtherVersionsAsync(packageId, installed.Version, cancellationToken).ConfigureAwait(false);

        liveState.PipelinesChanged();
        return installed;
    }

    /// <summary>
    /// Stops every installed version of <paramref name="packageId"/> being listed or run. Runs already
    /// in flight finish on the copy their agent already has; agents' caches are left to their own eviction.
    /// </summary>
    /// <exception cref="RpcException"><see cref="StatusCode.NotFound"/> when no version is installed.</exception>
    public async Task UninstallAsync(string packageId, CancellationToken cancellationToken)
    {
        if (!await catalogService.UninstallAsync(packageId, cancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"No version of '{packageId}' is installed."));
        }

        liveState.PipelinesChanged();
    }

    /// <summary>
    /// Sends an install of <paramref name="packageId"/> <paramref name="version"/> to a connected agent
    /// and waits for its result. <paramref name="version"/> is null when nothing installable was resolved.
    /// </summary>
    private async Task<InstalledPackage> InstallOnAnAgentAsync(string packageId, string? version, CancellationToken cancellationToken)
    {
        if (!connections.TryGetAnyConnectedAgentId(out var agentId))
        {
            throw ServiceScaffolding.NoAgentsAvailable();
        }

        if (version is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound, $"No installable version of '{packageId}' was found on the configured feed."));
        }

        var feedUrls = await catalogService.GetFeedUrlsAsync(cancellationToken).ConfigureAwait(false);

        InstallDispatchResult result;
        try
        {
            result = await connections
                .DispatchInstallPackageAsync(agentId, packageId, version, feedUrls, InstallTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(
                StatusCode.DeadlineExceeded, $"Agent '{agentId}' did not report an install result within {InstallTimeout}."));
        }

        if (!result.Succeeded)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"Install failed on agent '{agentId}': {result.Error}"));
        }

        return new InstalledPackage(result.PackageVersionId!.Value, version, result.PipelineNames);
    }
}
