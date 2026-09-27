using EtlPipelines.Management.V1;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Server.Agents;

/// <summary>
/// The operational gap <see cref="AgentConnectionRegistry"/>'s own remarks flag as unaddressed
/// until now: an agent whose process died (not merely one that called <c>Disconnect</c> by
/// dropping its <c>Subscribe</c> stream cleanly) stops heartbeating but otherwise leaves no trace
/// - without this, its <c>Runs</c> row sits <c>Dispatched</c>/<c>Running</c> forever, and
/// <c>ListAgents</c> would keep reporting it <c>Online</c> (SERVER.md Phase 8).
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/> on its own timer, independent of any one gRPC call - an
/// agent that stops heartbeating is, by definition, not making any other call either, so nothing
/// request-driven could ever notice this. Takes a fresh DI scope every sweep (the same reasoning
/// <see cref="Catalog.NuGetFeedSeeder"/>'s own remarks give: <see cref="ServerDbContext"/> is
/// scoped, and this service outlives any one scope). <see cref="SweepAsync"/> is public and takes
/// <see cref="TimeProvider"/> as a constructor dependency specifically so a fast test can drive
/// the timeout decision with a fake "now" rather than a real elapsed 30 seconds - see
/// <c>AgentLivenessMonitorTests</c>.
/// </remarks>
public sealed class AgentLivenessMonitor(
    IServiceScopeFactory scopeFactory,
    IOptions<AgentLivenessOptions> options,
    RunStatusStore statusStore,
    TimeProvider timeProvider,
    ILogger<AgentLivenessMonitor> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval, timeProvider);

        do
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep (a transient database blip) never stops future ones - the next
                // tick tries again, same as every other periodic loop in this codebase
                // (AgentRegistration's own SendHeartbeatsAsync included).
                logger.LogWarning(exception, "Agent liveness sweep failed; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Marks every agent whose <c>LastHeartbeatAt</c> is older than <see cref="AgentLivenessOptions.Timeout"/>
    /// <c>Offline</c>, and any <c>Runs</c> row still owned by an agent that far behind
    /// <c>AgentLost</c> - joined against the agent's own heartbeat directly (not gated on the
    /// agent's <c>Status</c> field already being <c>Online</c>), so a run left behind by an
    /// earlier, already-completed sweep is still caught here rather than only ever being handled
    /// the one tick its agent's status actually flipped.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ServerDbContext>();

        var cutoff = timeProvider.GetUtcNow().UtcDateTime - options.Value.Timeout;

        var staleAgents = await dbContext.Agents
            .Where(agent => agent.Status == "Online" && agent.LastHeartbeatAt < cutoff)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var agent in staleAgents)
        {
            agent.Status = "Offline";
        }

        // Runs.AgentId FK -> Agent.LastHeartbeatAt directly, not staleAgents above - a run left
        // stuck by a sweep that only got as far as flipping its agent Offline before faulting
        // (the try/catch in ExecuteAsync) is still found and finished off by the very next tick,
        // rather than never being revisited because its agent's Status no longer reads "Online".
        var lostRuns = await dbContext.Runs
            .Where(run =>
                run.AgentId != null && run.Agent!.LastHeartbeatAt < cutoff &&
                (run.Status == "Queued" || run.Status == "Dispatched" || run.Status == "Running"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (staleAgents.Count == 0 && lostRuns.Count == 0)
        {
            return;
        }

        var completedAt = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var run in lostRuns)
        {
            // Left there deliberately - SERVER.md Phase 8: "A Run marked AgentLost stays there.
            // Retrying a partially applied load without knowing whether it's idempotent would be
            // actively dangerous." This sweep's whole job is surfacing that state, never resolving it.
            run.Status = "AgentLost";
            run.CompletedAt = completedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var run in lostRuns)
        {
            // So a StreamRunProgress caller still watching this run learns it is over, the same
            // way AgentServiceImpl.ReportExecutionStatus's own EXITED fallback already does for a
            // process that exited without managing to report itself.
            statusStore.PublishRunCompleted(run.Id, new RunCompleted { Status = RunCompleted.Types.Status.AgentLost });
        }

        logger.LogWarning(
            "Marked {AgentCount} agent(s) offline and {RunCount} run(s) AgentLost after {Timeout} without a heartbeat.",
            staleAgents.Count, lostRuns.Count, options.Value.Timeout);
    }
}
