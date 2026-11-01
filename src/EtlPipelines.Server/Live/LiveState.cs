using System.Collections.Concurrent;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Live.Events;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Live;

/// <summary>
/// The server's one store of what is happening: every agent, and every run that is in flight or
/// finished within the last <see cref="Retention"/> - with its stages and resource samples - held in
/// memory, plus a feed of what changed, which is pushed to the WebUI and to <c>StreamRunProgress</c>
/// callers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Memory is the source of truth for anything in flight.</b> A change is made by a command
/// (<see cref="CreateRunAsync"/>, <see cref="StageCompletedAsync"/>, ...), which decides whether it
/// applies and, if so, turns it into an immutable <see cref="LiveEvent"/>. The event is applied to the
/// state and appended to an ordered outbox in one step, under one lock, so the state and what will
/// be persisted cannot disagree.
/// </para>
/// <para>
/// <b>The database is written behind.</b> <see cref="LiveStateSync"/> takes the outbox every few
/// seconds and replays its events, in order, into the tables in one batch. Because it replays events
/// rather than copying the latest state, nothing that happened between two syncs is lost: a run that
/// is created, started, reports stages and finishes inside one window still has all of it written.
/// After a restart the state is read back from the database by <see cref="LoadAsync"/>; the events
/// not yet written when the process died (at most one sync interval of them) are the only thing lost.
/// Everything in flight is read from here, never from the database, which can be behind - callers wait
/// for <see cref="LoadAsync"/> to finish first, so a request that arrives during startup sees the
/// loaded state, not an empty one.
/// </para>
/// <para>
/// Runs older than <see cref="Retention"/> are not held; the methods that can reach them take the
/// database context and read it for those.
/// </para>
/// </remarks>
public sealed class LiveState(TimeProvider timeProvider, ILogger<LiveState> logger)
{
    /// <summary>How long a finished run is kept in memory.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly EventOutbox _outbox = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _subscriptionsGate = new();
    private readonly List<LiveSubscription> _subscriptions = [];

    // Pipelines whose history was cleared and where the delete is not in the database yet, with how many
    // such clears are waiting. Until then the database still has those runs, and anything read from it
    // has to leave them out.
    private readonly Dictionary<Guid, int> _pendingClears = [];

    private volatile ConcurrentDictionary<Guid, LiveRun> _runs = new();
    private volatile ConcurrentDictionary<Guid, LiveAgent> _agents = new();

    /// <summary>How many events are waiting to be written to the database.</summary>
    public int PendingEventCount => _outbox.Count;

    /// <summary>How many subscriptions are listening.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_subscriptionsGate)
            {
                return _subscriptions.Count;
            }
        }
    }

    // ---- listening ----------------------------------------------------------------------

    /// <summary>Starts listening to changes; <paramref name="filter"/> narrows it to the ones of interest. Dispose it to stop.</summary>
    public LiveSubscription Subscribe(Func<LiveChange, bool>? filter = null)
    {
        var subscription = new LiveSubscription(filter ?? (_ => true), Unsubscribe);
        lock (_subscriptionsGate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void Unsubscribe(LiveSubscription subscription)
    {
        lock (_subscriptionsGate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private void Publish(IEnumerable<LiveChange> changes)
    {
        LiveSubscription[] subscribers;
        lock (_subscriptionsGate)
        {
            subscribers = [.. _subscriptions];
        }

        foreach (var change in changes.Distinct())
        {
            foreach (var subscriber in subscribers)
            {
                subscriber.Offer(change);
            }
        }
    }

    /// <summary>Tells listeners the set of installed pipelines changed.</summary>
    public void PipelinesChanged() => Publish([new LiveChange(LiveChangeKind.Pipelines, Guid.Empty)]);

    // ---- loading ------------------------------------------------------------------------

    /// <summary>
    /// Replaces what is held with what the database has - every agent, and the runs in flight or
    /// finished within <see cref="Retention"/>, with their stages and samples - and lets waiting
    /// callers go. It also discards any event not yet written, so it is for startup (and tests).
    /// </summary>
    public async Task LoadAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        var agents = new ConcurrentDictionary<Guid, LiveAgent>();
        foreach (var agent in await db.Agents.AsNoTracking().Where(agent => agent.Status != "Removed").ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            agents[agent.Id] = LiveAgent.From(agent);
        }

        var cutoff = timeProvider.GetUtcNow().UtcDateTime - Retention;
        var runIds = await db.Runs
            .AsNoTracking()
            .Where(run => RunQueries.ActiveStatuses.Contains(run.Status) || run.CompletedAt >= cutoff)
            .Select(run => run.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var runs = new ConcurrentDictionary<Guid, LiveRun>();
        foreach (var runId in runIds)
        {
            if (await RunQueries.LoadDetailAsync(db, runId, cancellationToken).ConfigureAwait(false) is { } detail)
            {
                runs[runId] = LiveRun.From(detail);
            }
        }

        lock (_gate)
        {
            _agents = agents;
            _runs = runs;
            _outbox.Clear();
            _pendingClears.Clear();
        }

        _loaded.TrySetResult();
        logger.LogInformation("Loaded {AgentCount} agent(s) and {RunCount} run(s) into the live state.", agents.Count, runs.Count);
    }

    private Task WhenLoadedAsync(CancellationToken cancellationToken) =>
        _loaded.Task.IsCompleted ? Task.CompletedTask : _loaded.Task.WaitAsync(cancellationToken);

    // ---- commands -----------------------------------------------------------------------

    /// <summary>
    /// An agent registers. Returns the id it is known by from now on: <paramref name="requestedId"/> when the
    /// agent brought the one it was issued before (it is then the same agent - same tags a user gave it, same
    /// history - however many times it has restarted, and even if it had been removed), a new one when it
    /// did not.
    /// </summary>
    public async Task<Guid> RegisterAgentAsync(
        Guid? requestedId, string machineName, IReadOnlyList<string> tags, string version, string? infoJson, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var agentId = requestedId ?? Guid.NewGuid();
        Append(new AgentRegisteredEvent(agentId, machineName, tags, version, infoJson, Now()));
        return agentId;
    }

    /// <summary>
    /// Sets the tags a user gives an agent (replacing the earlier ones). The caller has normalized them
    /// (<see cref="TagNames.TryNormalize"/>). False when no such agent is held.
    /// </summary>
    public async Task<bool> SetAgentTagsAsync(Guid agentId, IReadOnlyList<string> assignedTags, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return Append(() => _agents.ContainsKey(agentId) ? [new AgentTagsChangedEvent(agentId, assignedTags, Now())] : []);
    }

    /// <summary>An agent heartbeated. False when no such agent is known (it is left alone).</summary>
    public async Task<bool> AgentHeartbeatAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return Append(() => _agents.ContainsKey(agentId) ? [new AgentHeartbeatEvent(agentId, Now())] : []);
    }

    /// <summary>
    /// Marks every agent that has not heartbeated within <paramref name="timeout"/> offline, and every
    /// run still in flight on such an agent lost. A lost run is never retried: a partial load may not
    /// be safe to repeat.
    /// </summary>
    public async Task<LiveSweepResult> SweepAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var result = default(LiveSweepResult);
        Append(() =>
        {
            var now = Now();
            var cutoff = now - timeout;
            var events = new List<LiveEvent>();

            var staleAgents = _agents.Values
                .Where(agent => agent.Status == "Online" && agent.LastHeartbeatAtUtc < cutoff)
                .Select(agent => agent.Id)
                .ToList();
            events.AddRange(staleAgents.Select(id => new AgentOfflineEvent(id, now)));

            // Judged by the agent's own last heartbeat - not by whether this sweep is the one that
            // flipped it offline - so a run is caught however its agent got there.
            var lostRuns = _runs.Values
                .Where(run => run.IsActive && run.AgentId is { } agentId && _agents.TryGetValue(agentId, out var agent) && agent.LastHeartbeatAtUtc < cutoff)
                .Select(run => run.Id)
                .ToList();
            events.AddRange(lostRuns.Select(id => new RunFinishedEvent(id, "AgentLost", null, null, null, null, now)));

            result = new LiveSweepResult(staleAgents.Count, lostRuns.Count);
            return events;
        });

        return result;
    }

    /// <summary>
    /// Removes an agent that has stopped heartbeating, so it is no longer listed - the clean-up for the
    /// registrations a restarted or retired agent leaves behind. An agent that is online, or still has
    /// runs in flight, is refused. The runs it carried out keep its name.
    /// </summary>
    public async Task<AgentRemovalResult> RemoveAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var result = AgentRemovalResult.NotFound;
        Append(() =>
        {
            if (!_agents.TryGetValue(agentId, out var agent))
            {
                return [];
            }

            if (agent.Status == "Online")
            {
                result = AgentRemovalResult.StillOnline;
                return [];
            }

            if (_runs.Values.Any(run => run.IsActive && run.AgentId == agentId))
            {
                result = AgentRemovalResult.HasActiveRuns;
                return [];
            }

            result = AgentRemovalResult.Removed;
            return [new AgentRemovedEvent(agentId, Now())];
        });

        return result;
    }

    /// <summary>
    /// Deletes the finished runs of a pipeline - with their stages and samples - from memory now and the
    /// database at the next sync. Runs in flight are left alone.
    /// </summary>
    public async Task ClearHistoryAsync(Guid pipelineId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        Append(new PipelineHistoryClearedEvent(pipelineId, Now()));
    }

    /// <summary>A run is created and dispatched. Returns its id.</summary>
    public async Task<Guid> CreateRunAsync(
        Guid pipelineId,
        string pipelineName,
        string packageId,
        string packageVersion,
        Guid? agentId,
        IReadOnlyList<string> expectedStages,
        CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var runId = Guid.NewGuid();
        Append(new RunCreatedEvent(runId, pipelineId, pipelineName, packageId, packageVersion, agentId, expectedStages, Now()));
        return runId;
    }

    /// <summary>
    /// A run's process started. False when no such run is held; otherwise true, whether or not
    /// anything changed (a run that is already running with a start time is left as it is).
    /// </summary>
    public async Task<bool> StartRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var found = false;
        Append(() =>
        {
            if (!_runs.TryGetValue(runId, out var run))
            {
                return [];
            }

            found = true;
            return run.Status is "Queued" or "Dispatched" || run.StartedAtUtc is null ? [new RunStartedEvent(runId, Now())] : [];
        });

        return found;
    }

    /// <summary>A run completed a stage. False when no such run is held.</summary>
    public async Task<bool> StageCompletedAsync(Guid runId, StageDto stage, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return Append(() => _runs.ContainsKey(runId) ? [new StageCompletedEvent(runId, Guid.NewGuid(), stage, Now())] : []);
    }

    /// <summary>An agent sampled a run's process. False when no such run is held.</summary>
    public async Task<bool> ResourceSampledAsync(
        Guid runId, Guid agentId, double cpuPercent, long workingSetBytes, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return Append(() => _runs.ContainsKey(runId)
            ? [new ResourceSampledEvent(runId, agentId, Guid.NewGuid(), cpuPercent, workingSetBytes, Now())]
            : []);
    }

    /// <summary>A run reported its own outcome. False when no such run is held.</summary>
    public async Task<bool> FinishRunAsync(
        Guid runId, string status, int exitCode, long rowsRead, long rowsWritten, long rowsFailed, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return Append(() => _runs.ContainsKey(runId)
            ? [new RunFinishedEvent(runId, status, exitCode, rowsRead, rowsWritten, rowsFailed, Now())]
            : []);
    }

    /// <summary>
    /// An agent saw a run's process exit. This is only a fallback for a process that died without
    /// reporting its own outcome, so it ends the run only if it is still in flight - one that already
    /// reported is left as it is. True if it ended the run.
    /// </summary>
    public async Task<bool> ProcessExitedAsync(Guid runId, int exitCode, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return Append(() =>
        {
            if (!_runs.TryGetValue(runId, out var run) || !run.IsActive)
            {
                return [];
            }

            var status = exitCode == 0 ? "Succeeded" : "Failed";
            return [new RunFinishedEvent(runId, status, exitCode, null, null, null, Now())];
        });
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Applies and queues one event.</summary>
    private void Append(LiveEvent liveEvent) => Append(() => [liveEvent]);

    /// <summary>
    /// Decides what events a command amounts to, applies them and queues them, all under one lock - so
    /// the decision, the state and the outbox cannot be interleaved with another command's - and then
    /// tells listeners. True if there was anything to apply.
    /// </summary>
    private bool Append(Func<IReadOnlyList<LiveEvent>> decide)
    {
        var changes = new List<LiveChange>();
        bool any;

        lock (_gate)
        {
            var events = decide();
            any = events.Count > 0;

            foreach (var liveEvent in events)
            {
                Apply(liveEvent, changes);
                _outbox.Add(liveEvent);
            }
        }

        Publish(changes);
        return any;
    }

    /// <summary>The one place an event changes the in-memory state. Called under <see cref="_gate"/>.</summary>
    private void Apply(LiveEvent liveEvent, List<LiveChange> changes)
    {
        switch (liveEvent)
        {
            case AgentRegisteredEvent registered:
                _agents[registered.AgentId] = new LiveAgent(
                    registered.AgentId,
                    registered.MachineName,
                    registered.Tags,
                    registered.Version,
                    "Online",
                    registered.AtUtc,
                    LiveAgent.ParseInfo(registered.InfoJson),
                    // Registering again is the same agent starting over: what a user gave it stays.
                    _agents.TryGetValue(registered.AgentId, out var before) ? before.AssignedTags : []);
                changes.Add(new LiveChange(LiveChangeKind.Agent, registered.AgentId));
                break;

            case AgentTagsChangedEvent tagged when _agents.TryGetValue(tagged.AgentId, out var agent):
                _agents[tagged.AgentId] = agent with { AssignedTags = tagged.AssignedTags };
                changes.Add(new LiveChange(LiveChangeKind.Agent, tagged.AgentId));
                break;

            case AgentHeartbeatEvent heartbeat when _agents.TryGetValue(heartbeat.AgentId, out var agent):
                _agents[heartbeat.AgentId] = agent with { Status = "Online", LastHeartbeatAtUtc = heartbeat.AtUtc };
                changes.Add(new LiveChange(LiveChangeKind.Agent, heartbeat.AgentId));
                break;

            case AgentOfflineEvent offline when _agents.TryGetValue(offline.AgentId, out var agent):
                _agents[offline.AgentId] = agent with { Status = "Offline" };
                changes.Add(new LiveChange(LiveChangeKind.Agent, offline.AgentId));
                break;

            case AgentRemovedEvent removed:
                _agents.TryRemove(removed.AgentId, out _);
                changes.Add(new LiveChange(LiveChangeKind.AgentRemoved, removed.AgentId));
                break;

            case PipelineHistoryClearedEvent cleared:
                _pendingClears[cleared.PipelineId] = _pendingClears.GetValueOrDefault(cleared.PipelineId) + 1;
                foreach (var (runId, held) in _runs)
                {
                    if (held.PipelineId == cleared.PipelineId && !held.IsActive)
                    {
                        _runs.TryRemove(runId, out _);
                    }
                }

                // The runs lists and the pipeline's last run changed, which is what listeners refetch on.
                changes.Add(new LiveChange(LiveChangeKind.Pipelines, Guid.Empty));
                break;

            case RunCreatedEvent created:
                var agentName = created.AgentId is { } agentId && _agents.TryGetValue(agentId, out var owner) ? owner.MachineName : null;
                var createdRun = LiveRun.Create(created, agentName);
                _runs[created.RunId] = createdRun;
                AddRunChanges(changes, createdRun);
                break;

            case RunStartedEvent started when _runs.TryGetValue(started.RunId, out var run):
                run.Start(started.AtUtc);
                AddRunChanges(changes, run);
                break;

            case RunFinishedEvent finished when _runs.TryGetValue(finished.RunId, out var run):
                run.Finish(finished.Status, finished.ExitCode, finished.RowsRead, finished.RowsWritten, finished.RowsFailed, finished.AtUtc);
                AddRunChanges(changes, run);
                EvictExpired();
                break;

            case StageCompletedEvent stage when _runs.TryGetValue(stage.RunId, out var run):
                run.AddStage(stage.Stage);
                AddRunChanges(changes, run);
                break;

            case ResourceSampledEvent sample when _runs.TryGetValue(sample.RunId, out var run):
                run.AddSample(new ResourceSampleDto(sample.AtUtc, sample.CpuPercent, sample.WorkingSetBytes));
                changes.Add(new LiveChange(LiveChangeKind.Run, sample.RunId));
                break;
        }
    }

    /// <summary>A run changed - and so did its agent, whose "running now" list shows the run.</summary>
    private static void AddRunChanges(List<LiveChange> changes, LiveRun run)
    {
        changes.Add(new LiveChange(LiveChangeKind.Run, run.Id));
        if (run.AgentId is { } agentId)
        {
            changes.Add(new LiveChange(LiveChangeKind.Agent, agentId));
        }
    }

    private void EvictExpired()
    {
        var cutoff = Now() - Retention;
        foreach (var (runId, run) in _runs)
        {
            if (!run.IsActive && run.CompletedAtUtc < cutoff)
            {
                _runs.TryRemove(runId, out _);
            }
        }
    }

    // ---- syncing to the database --------------------------------------------------------

    /// <summary>Takes every event not yet written to the database, oldest first. See <see cref="LiveStateSync"/>.</summary>
    public IReadOnlyList<LiveEvent> DrainPendingEvents()
    {
        // Under the same lock as every command, so a batch is always cut between commands: the events
        // of one (a sweep's offline agents and lost runs, say) are never split across two batches.
        lock (_gate)
        {
            return _outbox.Drain();
        }
    }

    /// <summary>Records that <paramref name="events"/> are now in the database (or have been given up on), so what they deleted need no longer be hidden.</summary>
    public void MarkWritten(IReadOnlyList<LiveEvent> events)
    {
        lock (_gate)
        {
            foreach (var pipelineId in events.OfType<PipelineHistoryClearedEvent>().Select(cleared => cleared.PipelineId))
            {
                var remaining = _pendingClears.GetValueOrDefault(pipelineId) - 1;
                if (remaining > 0)
                {
                    _pendingClears[pipelineId] = remaining;
                }
                else
                {
                    _pendingClears.Remove(pipelineId);
                }
            }
        }
    }

    /// <summary>Whether a finished run of <paramref name="pipelineId"/> the database still has is one whose clear has not been written yet.</summary>
    private bool IsDeletedPending(Guid pipelineId, string status)
    {
        lock (_gate)
        {
            return _pendingClears.ContainsKey(pipelineId) && !RunQueries.IsActive(status);
        }
    }

    /// <summary>Puts events that could not be written back, ahead of any that happened since.</summary>
    public void RequeuePendingEvents(IReadOnlyList<LiveEvent> events)
    {
        lock (_gate)
        {
            _outbox.Requeue(events);
        }
    }

    // ---- reads --------------------------------------------------------------------------

    /// <summary>The runs in flight, newest first, from memory.</summary>
    public async Task<IReadOnlyList<RunSummaryDto>> ActiveRunsAsync(Guid? pipelineId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return _runs.Values
            .Where(run => run.IsActive && (pipelineId is null || run.PipelineId == pipelineId))
            .Select(run => run.ToSummary())
            .OrderByDescending(run => run.RequestedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Runs, newest first: what memory holds, merged with what the database has (which can be a few
    /// seconds behind, and has runs older than memory keeps). Where both have a run, memory wins.
    /// </summary>
    public async Task<IReadOnlyList<RunSummaryDto>> ListRunsAsync(
        ServerDbContext db, bool? active, Guid? pipelineId, int limit, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var held = _runs.Values
            .Where(run => (active is null || run.IsActive == active) && (pipelineId is null || run.PipelineId == pipelineId))
            .Select(run => run.ToSummary())
            .ToList();

        if (active is true)
        {
            return [.. held.OrderByDescending(run => run.RequestedAtUtc).Take(limit)];
        }

        var heldIds = _runs.Keys.ToHashSet();
        var stored = await RunQueries.ListAsync(db, active, pipelineId, limit, cancellationToken).ConfigureAwait(false);

        return [.. held
            .Concat(stored.Where(run => !heldIds.Contains(run.Id) && !IsDeletedPending(run.PipelineId, run.Status)))
            .OrderByDescending(run => run.RequestedAtUtc)
            .Take(limit)];
    }

    /// <summary>One run with its stages and samples: from memory if it is held, otherwise from the database.</summary>
    public async Task<RunDetailDto?> GetRunAsync(ServerDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (TryGetRun(runId) is { } held)
        {
            return held;
        }

        var stored = await RunQueries.LoadDetailAsync(db, runId, cancellationToken).ConfigureAwait(false);
        return stored is not null && IsDeletedPending(stored.Run.PipelineId, stored.Run.Status) ? null : stored;
    }

    /// <summary>Whether a run is held - which every run in flight is. Waits for the state to be loaded.</summary>
    public async Task<bool> HoldsRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _runs.ContainsKey(runId);
    }

    /// <summary>One run's summary, if it is held in memory.</summary>
    public RunSummaryDto? TryGetRunSummary(Guid runId) => _runs.TryGetValue(runId, out var run) ? run.ToSummary() : null;

    /// <summary>One run with its stages and samples, if it is held in memory.</summary>
    public RunDetailDto? TryGetRun(Guid runId) => _runs.TryGetValue(runId, out var run) ? run.ToDetail() : null;

    /// <summary>How many runs of <paramref name="pipelineId"/> are in flight.</summary>
    public async Task<int> ActiveRunCountAsync(Guid pipelineId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        return _runs.Values.Count(run => run.PipelineId == pipelineId && run.IsActive);
    }

    /// <summary>
    /// The latest run of <paramref name="pipelineId"/>: from memory when it has one in flight or
    /// recent, otherwise from the database.
    /// </summary>
    public async Task<RunSummaryDto?> LastRunAsync(ServerDbContext db, Guid pipelineId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var held = _runs.Values
            .Where(run => run.PipelineId == pipelineId)
            .Select(run => run.ToSummary())
            .OrderByDescending(run => run.RequestedAtUtc)
            .FirstOrDefault();

        if (held is not null)
        {
            return held;
        }

        var stored = await RunQueries.LastRunAsync(db, pipelineId, cancellationToken).ConfigureAwait(false);
        return stored is not null && IsDeletedPending(pipelineId, stored.Status) ? null : stored;
    }

    /// <summary>
    /// The stage names of <paramref name="pipelineId"/>'s last successful run, in order - what a new
    /// run of it is expected to go through. From memory when it holds such a run, otherwise the database.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExpectedStagesAsync(ServerDbContext db, Guid pipelineId, CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var lastSucceeded = _runs.Values
            .Where(run => run.PipelineId == pipelineId && run.Status == "Succeeded")
            .OrderByDescending(run => run.CompletedAtUtc)
            .Select(run => run.ToDetail())
            .FirstOrDefault();

        if (lastSucceeded is not null)
        {
            return lastSucceeded.Stages.Select(stage => stage.Name).ToList();
        }

        // The database's, unless its history was just cleared and the delete is still to be written.
        return IsDeletedPending(pipelineId, "Succeeded")
            ? []
            : await RunQueries.ExpectedStageNamesAsync(db, pipelineId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every agent, by machine name, from memory.</summary>
    public async Task<IReadOnlyList<AgentDto>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        return _agents.Values
            .OrderBy(agent => agent.MachineName, StringComparer.Ordinal)
            .ThenBy(agent => agent.LastHeartbeatAtUtc)
            .Select(agent => agent.ToDto(now, ActiveRunsOf(agent.Id)))
            .ToList();
    }

    /// <summary>Whether <paramref name="agentId"/> is a known agent (one that was never registered, or was removed, is not).</summary>
    public bool IsKnownAgent(Guid agentId) => _agents.ContainsKey(agentId);

    /// <summary>The tags and current workload of every agent held - what choosing an agent for a run goes on.</summary>
    public async Task<IReadOnlyList<AgentLoad>> AgentLoadsAsync(CancellationToken cancellationToken)
    {
        await WhenLoadedAsync(cancellationToken).ConfigureAwait(false);

        var active = _runs.Values
            .Where(run => run.IsActive && run.AgentId is not null)
            .GroupBy(run => run.AgentId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        return _agents.Values
            .Select(agent => new AgentLoad(agent.Id, agent.AllTags, active.GetValueOrDefault(agent.Id)))
            .ToList();
    }

    /// <summary>One agent, if it is held in memory.</summary>
    public AgentDto? TryGetAgent(Guid agentId) =>
        _agents.TryGetValue(agentId, out var agent) ? agent.ToDto(timeProvider.GetUtcNow(), ActiveRunsOf(agentId)) : null;

    private List<RunSummaryDto> ActiveRunsOf(Guid agentId) =>
        _runs.Values
            .Where(run => run.IsActive && run.AgentId == agentId)
            .Select(run => run.ToSummary())
            .OrderByDescending(run => run.RequestedAtUtc)
            .ToList();
}
