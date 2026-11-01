using EtlPipelines.Server.Live;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Server.Agents;

/// <summary>
/// The operational gap <see cref="AgentConnectionRegistry"/>'s own remarks flag: an agent whose
/// process died (not merely one that dropped its <c>Subscribe</c> stream cleanly) stops heartbeating
/// but otherwise leaves no trace - without this, its runs sit <c>Dispatched</c>/<c>Running</c> forever,
/// and <c>ListAgents</c> would keep reporting it <c>Online</c> (SERVER.md Phase 8).
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/> on its own timer, independent of any one gRPC call - an agent
/// that stops heartbeating is, by definition, not making any other call either, so nothing
/// request-driven could ever notice this. It works on <see cref="LiveState"/>, which knows every
/// agent's last heartbeat the moment it arrives (the database can be a few seconds behind), so a
/// sweep reads no database at all; what it changes is written there by <see cref="LiveStateSync"/>
/// like everything else. <see cref="SweepAsync"/> is public, and the clock is
/// <see cref="LiveState"/>'s, specifically so a fast test can drive the timeout with a fake "now"
/// rather than a real elapsed 30 seconds - see <c>AgentLivenessMonitorTests</c>.
/// </remarks>
public sealed class AgentLivenessMonitor(
    LiveState liveState,
    IOptions<AgentLivenessOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentLivenessMonitor> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Agents were last heard from before this server started, which may be long ago: they reconnect and
        // heartbeat again within a few seconds of it being back. Judging them straight away would mark every one
        // offline, and every run still going on them lost, on each restart of the server. So they are given as long
        // as the timeout to be heard from first.
        await Task.Delay(options.Value.Timeout, timeProvider, stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.Value.PollInterval, timeProvider);

        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep never stops future ones - the next tick tries again, same as every
                // other periodic loop in this codebase (AgentRegistration's own SendHeartbeatsAsync included).
                logger.LogWarning(exception, "Agent liveness sweep failed; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Marks every agent whose last heartbeat is older than <see cref="AgentLivenessOptions.Timeout"/>
    /// <c>Offline</c>, and any run still in flight on an agent that far behind <c>AgentLost</c> -
    /// judged against the agent's own heartbeat directly (not gated on its status already reading
    /// <c>Online</c>), so a run is caught however its agent came to be silent.
    /// </summary>
    /// <remarks>
    /// A run marked <c>AgentLost</c> stays there. SERVER.md Phase 8: retrying a partially applied load
    /// without knowing whether it is idempotent would be actively dangerous - this sweep's whole job
    /// is surfacing that state, never resolving it. Anyone watching the run through
    /// <c>StreamRunProgress</c> or the WebUI is told it is over by the change feed.
    /// </remarks>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var result = await liveState.SweepAsync(options.Value.Timeout, cancellationToken).ConfigureAwait(false);

        if (result.AgentsMarkedOffline > 0 || result.RunsMarkedLost > 0)
        {
            logger.LogWarning(
                "Marked {AgentCount} agent(s) offline and {RunCount} run(s) AgentLost after {Timeout} without a heartbeat.",
                result.AgentsMarkedOffline, result.RunsMarkedLost, options.Value.Timeout);
        }
    }
}
