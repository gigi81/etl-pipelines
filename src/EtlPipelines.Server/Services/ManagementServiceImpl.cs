using EtlPipelines.Management.V1;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Secrets;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

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
/// <see cref="PipelineExecutionServiceImpl"/> as the launched process reports in, and falling back
/// to <c>Runs</c>/<c>StageResults</c> once a finished run's history has been evicted from it).
/// <see cref="ListAgents"/> reads the <c>Agents</c> table directly. <see cref="UninstallPackage"/>
/// and <see cref="UpdatePackage"/> retire package versions through <c>PackageVersions.Status</c>
/// (<see cref="PackageVersionStatus"/>) rather than deleting rows <c>Runs</c> still reference.
/// </remarks>
public sealed class ManagementServiceImpl(
    ServerDbContext dbContext,
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
        var version = await catalogService
            .ResolveVersionAsync(request.PackageId, request.Version, context.CancellationToken)
            .ConfigureAwait(false);

        var installed = await InstallOnAnAgentAsync(request.PackageId, version, context.CancellationToken).ConfigureAwait(false);

        var response = new InstallPackageResponse
        {
            PackageVersionId = installed.PackageVersionId.ToString(),
            Version = installed.Version,
        };
        response.PipelineNames.AddRange(installed.PipelineNames);
        return response;
    }

    /// <inheritdoc />
    public override async Task<Ack> UninstallPackage(UninstallPackageRequest request, ServerCallContext context)
    {
        if (!await catalogService.UninstallAsync(request.PackageId, context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"No version of '{request.PackageId}' is installed."));
        }

        return new Ack();
    }

    /// <inheritdoc />
    public override async Task<UpdatePackageResponse> UpdatePackage(UpdatePackageRequest request, ServerCallContext context)
    {
        if (!await catalogService.IsInstalledAsync(request.PackageId, context.CancellationToken).ConfigureAwait(false))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition, $"'{request.PackageId}' is not installed - install it before updating it."));
        }

        var latest = await catalogService
            .ResolveVersionAsync(request.PackageId, requestedVersion: null, context.CancellationToken)
            .ConfigureAwait(false);

        // Already on the latest version: nothing to send to an agent, so this works with none
        // connected - only retiring the older versions below still applies.
        var current = latest is null
            ? null
            : await catalogService.FindInstalledVersionAsync(request.PackageId, latest, context.CancellationToken).ConfigureAwait(false);

        var installed = current is not null
            ? new InstalledPackage(current.PackageVersionId, latest!, current.PipelineNames)
            : await InstallOnAnAgentAsync(request.PackageId, latest, context.CancellationToken).ConfigureAwait(false);

        await catalogService
            .SupersedeOtherVersionsAsync(request.PackageId, installed.Version, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new UpdatePackageResponse
        {
            PackageVersionId = installed.PackageVersionId.ToString(),
            Version = installed.Version,
        };
        response.PipelineNames.AddRange(installed.PipelineNames);
        return response;
    }

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

        // Not in the store: either a finished run whose history was already evicted, or a run id
        // nothing ever dispatched. Runs/StageResults hold everything the store would have
        // replayed for a finished run, so answer from there rather than subscribing to a run that
        // will never publish again.
        if (!statusStore.IsTracked(runId))
        {
            var run = await dbContext.Runs
                .AsNoTracking()
                .Include(r => r.StageResults)
                .SingleOrDefaultAsync(r => r.Id == runId, context.CancellationToken)
                .ConfigureAwait(false)
                ?? throw new RpcException(new Status(StatusCode.NotFound, $"No run with id '{runId}' exists."));

            var terminalStatus = ToRunCompletedStatus(run.Status);
            if (terminalStatus is not null)
            {
                foreach (var stage in run.StageResults.OrderBy(stage => stage.Sequence))
                {
                    await responseStream.WriteAsync(new RunProgressEvent
                    {
                        RunId = request.RunId,
                        StageCompleted = new StageCompleted
                        {
                            Sequence = stage.Sequence,
                            Name = stage.Name,
                            RowsIn = stage.RowsIn,
                            RowsOut = stage.RowsOut,
                            RowsFailed = stage.RowsFailed,
                            ElapsedMs = stage.ElapsedMs,
                            ErrorCode = stage.ErrorCode ?? string.Empty,
                            ErrorDescription = stage.ErrorDescription ?? string.Empty,
                        },
                    }).ConfigureAwait(false);
                }

                await responseStream.WriteAsync(new RunProgressEvent
                {
                    RunId = request.RunId,
                    RunCompleted = new RunCompleted
                    {
                        Status = terminalStatus.Value,
                        ExitCode = run.ExitCode ?? 0,
                        RowsRead = run.RowsRead ?? 0,
                        RowsWritten = run.RowsWritten ?? 0,
                        RowsFailed = run.RowsFailed ?? 0,
                    },
                }).ConfigureAwait(false);
                return;
            }
        }

        var reader = statusStore.Subscribe(runId);

        await foreach (var progressEvent in reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(progressEvent).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task<ListAgentsResponse> ListAgents(Empty request, ServerCallContext context)
    {
        var agents = await dbContext.Agents
            .AsNoTracking()
            .OrderBy(agent => agent.MachineName)
            .ThenBy(agent => agent.LastHeartbeatAt)
            .ToListAsync(context.CancellationToken)
            .ConfigureAwait(false);

        var response = new ListAgentsResponse();
        response.Agents.AddRange(agents.Select(agent => new AgentInfo
        {
            OsDescription = agent.OsDescription ?? string.Empty,
            OsArchitecture = agent.OsArchitecture ?? string.Empty,
            IpAddresses = { agent.IpAddresses ?? [] },
            ProcessorCount = agent.ProcessorCount ?? 0,
            StartedAt = agent.StartedAt is { } startedAt
                ? Timestamp.FromDateTime(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc))
                : null,
            AgentId = agent.Id.ToString(),
            MachineName = agent.MachineName,
            Tags = { agent.Tags },
            Version = agent.Version,
            Status = agent.Status switch
            {
                "Online" => AgentInfo.Types.Status.Online,
                "Offline" => AgentInfo.Types.Status.Offline,
                _ => AgentInfo.Types.Status.Unspecified,
            },
            LastHeartbeatAt = Timestamp.FromDateTime(DateTime.SpecifyKind(agent.LastHeartbeatAt, DateTimeKind.Utc)),
        }));
        return response;
    }

    /// <inheritdoc />
    public override async Task<Ack> SetConfigurationEntry(SetConfigurationEntryRequest request, ServerCallContext context)
    {
        await secretsStore.SetAsync(request.Key, request.Value, context.CancellationToken).ConfigureAwait(false);
        return new Ack();
    }

    /// <summary>
    /// Sends an install of <paramref name="packageId"/> <paramref name="version"/> to a connected
    /// agent and waits for its result - <see cref="InstallPackage"/> and <see cref="UpdatePackage"/>'s
    /// shared path. <paramref name="version"/> is null when nothing installable was resolved.
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

    /// <summary>Maps a terminal <c>Runs.Status</c> onto <see cref="RunCompleted.Types.Status"/>; <see langword="null"/> while the run is still in flight.</summary>
    private static RunCompleted.Types.Status? ToRunCompletedStatus(string status) => status switch
    {
        "Succeeded" => RunCompleted.Types.Status.Succeeded,
        "Failed" => RunCompleted.Types.Status.Failed,
        "AgentLost" => RunCompleted.Types.Status.AgentLost,
        _ => null,
    };
}

/// <summary>What <c>InstallPackage</c>/<c>UpdatePackage</c> answer with.</summary>
internal sealed record InstalledPackage(Guid PackageVersionId, string Version, IReadOnlyList<string> PipelineNames);
