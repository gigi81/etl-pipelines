using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;

namespace EtlPipelines.Server.Agents;

/// <summary>
/// Tracks which agents are currently connected (holding <c>AgentService.Subscribe</c> open) and
/// the install work items dispatched to them still awaiting a <c>ReportInstallResult</c>.
/// </summary>
/// <remarks>
/// In-memory only, deliberately: an agent's live stream cannot be persisted or resumed across a
/// <c>Server</c> restart, so there is nothing durable to keep here even if this class wanted to.
/// Phase 8 ("Reliability") is what makes an agent going silent recoverable, rather than just "the
/// dispatch times out" - <see cref="AgentLivenessMonitor"/> watches <c>Agents.LastHeartbeatAt</c>
/// (a durable, Server.Database-backed signal, unlike anything this class itself holds) and marks
/// any <c>Runs</c> row still owned by a silent agent <c>AgentLost</c>. A dispatch surviving a
/// <c>Server</c> restart is still exactly what this class's own remarks always said it could not
/// be - Phase 8 does not change that: every connection and pending install here is still lost the
/// moment this process restarts, same as before. Registered as a singleton: every gRPC call into
/// <c>AgentServiceImpl</c>/<c>ManagementServiceImpl</c> shares the one instance, since a connection or a pending dispatch
/// is process-wide state, not per-request.
/// </remarks>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Channel<WorkItem>> _connections = new();
    private readonly ConcurrentDictionary<string, PendingInstall> _pendingInstalls = new();

    /// <summary>Registers <paramref name="agentId"/> as connected, with <paramref name="channel"/> as its outbound work-item queue.</summary>
    public void Connect(string agentId, Channel<WorkItem> channel) => _connections[agentId] = channel;

    /// <summary>Removes <paramref name="agentId"/> - its <c>Subscribe</c> call has ended (client disconnect, cancellation, or fault).</summary>
    public void Disconnect(string agentId) => _connections.TryRemove(agentId, out _);

    /// <summary>
    /// Any one connected agent's id - selection is deliberately this simple (whichever happens to
    /// come out of the dictionary first) for now; load balancing across agents is a later
    /// concern, not this phase's.
    /// </summary>
    public bool TryGetAnyConnectedAgentId([NotNullWhen(true)] out string? agentId)
    {
        agentId = _connections.Keys.FirstOrDefault();
        return agentId is not null;
    }

    /// <summary>
    /// Sends an <c>InstallPackage</c> work item to <paramref name="agentId"/> and waits for that
    /// agent's own <c>ReportInstallResult</c> call to resolve it, up to <paramref name="timeout"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="agentId"/> is not currently connected.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="timeout"/> elapsed, or <paramref name="cancellationToken"/> fired, before the agent reported back.
    /// </exception>
    public async Task<InstallDispatchResult> DispatchInstallPackageAsync(
        string agentId,
        string packageId,
        string version,
        IReadOnlyList<string> feedUrls,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(agentId, out var channel))
        {
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");
        }

        var workItemId = Guid.NewGuid().ToString();
        var completion = new TaskCompletionSource<InstallDispatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingInstalls[workItemId] = new PendingInstall(packageId, version, completion);

        try
        {
            var workItem = new WorkItem
            {
                WorkItemId = workItemId,
                InstallPackage = new InstallPackageWorkItem { PackageId = packageId, Version = version },
            };
            workItem.InstallPackage.FeedUrls.AddRange(feedUrls);

            await channel.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            await using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingInstalls.TryRemove(workItemId, out _);
        }
    }

    /// <summary>
    /// Pushes an <c>ExecutePipeline</c> work item onto <paramref name="agentId"/>'s outbound
    /// queue. Fire-and-forget from this method's own point of view - unlike
    /// <see cref="DispatchInstallPackageAsync"/> there is no completion to await here:
    /// <c>ExecutePipelineResponse</c> hands the run id back immediately (see that RPC's own proto
    /// comment), and the launched pipeline process reports its own results back over
    /// <c>PipelineExecutionService</c> directly, never through this connection.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="agentId"/> is not currently connected, or stopped accepting work between the caller checking and this call.</exception>
    public void DispatchExecutePipeline(string agentId, ExecutePipelineWorkItem workItem)
    {
        if (!_connections.TryGetValue(agentId, out var channel))
        {
            throw new InvalidOperationException($"Agent '{agentId}' is not connected.");
        }

        var item = new WorkItem { WorkItemId = Guid.NewGuid().ToString(), ExecutePipeline = workItem };

        if (!channel.Writer.TryWrite(item))
        {
            throw new InvalidOperationException($"Agent '{agentId}' is no longer accepting work.");
        }
    }

    /// <summary>
    /// The package id/version <paramref name="workItemId"/> was dispatched for - what
    /// <c>AgentServiceImpl.ReportInstallResult</c> needs to record the result against
    /// <c>Server.Database</c>'s catalog tables, since <c>ReportInstallResultRequest</c> itself
    /// carries neither.
    /// </summary>
    public bool TryGetPendingInstall(string workItemId, out (string PackageId, string Version) pending)
    {
        if (_pendingInstalls.TryGetValue(workItemId, out var found))
        {
            pending = (found.PackageId, found.Version);
            return true;
        }

        pending = default;
        return false;
    }

    /// <summary>
    /// Resolves the dispatch <paramref name="workItemId"/> is waiting on. False if nothing is
    /// waiting - the dispatcher already timed out, or <paramref name="workItemId"/> is unknown
    /// (an agent reporting on a work item this server instance never issued, e.g. after a
    /// restart).
    /// </summary>
    public bool TryCompleteInstall(string workItemId, InstallDispatchResult result) =>
        _pendingInstalls.TryRemove(workItemId, out var pending) && pending.Completion.TrySetResult(result);

    private sealed record PendingInstall(string PackageId, string Version, TaskCompletionSource<InstallDispatchResult> Completion);
}

/// <summary>What an agent reported back for one dispatched <c>InstallPackage</c> work item.</summary>
public sealed record InstallDispatchResult(bool Succeeded, Guid? PackageVersionId, IReadOnlyList<string> PipelineNames, string? Error);
