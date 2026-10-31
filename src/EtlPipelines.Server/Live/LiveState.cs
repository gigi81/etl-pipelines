using System.Collections.Concurrent;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Live;

/// <summary>
/// What the server knows right now, held in memory: every agent, and every run that is in flight or
/// finished within the last <see cref="Retention"/> - with their stages and resource samples - plus a
/// feed of what changed, for the WebUI to be pushed to.
/// </summary>
/// <remarks>
/// <para>
/// The database stays the durable record. Every gRPC call that changes a run or an agent writes the
/// database first, exactly as before, and then tells this class, so reading the API never queries the
/// database for anything in flight and nothing polls it. What this holds is rebuilt from the
/// database on first use (so after a restart) and again after any update that fails - an update
/// is never allowed to fail the call that triggered it, it just marks the state for a reload.
/// Runs older than <see cref="Retention"/> are not held; they are read from the database when asked
/// for.
/// </para>
/// <para>
/// This is the REST/WebUI view. The gRPC <c>StreamRunProgress</c> is still served by
/// <see cref="Runs.RunStatusStore"/>.
/// </para>
/// </remarks>
public sealed class LiveState(TimeProvider timeProvider, ILogger<LiveState> logger)
{
    /// <summary>How long a finished run is kept in memory.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _subscriptionsGate = new();
    private readonly List<LiveSubscription> _subscriptions = [];

    private volatile ConcurrentDictionary<Guid, LiveRun> _runs = new();
    private volatile ConcurrentDictionary<Guid, LiveAgent> _agents = new();
    private volatile bool _loaded;

    /// <summary>How many subscriptions are listening - for tests.</summary>
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

    private void Publish(LiveChange change)
    {
        LiveSubscription[] subscribers;
        lock (_subscriptionsGate)
        {
            subscribers = [.. _subscriptions];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Offer(change);
        }
    }

    private void PublishRun(LiveRun run)
    {
        Publish(new LiveChange(LiveChangeKind.Run, run.Id));

        // The run is part of its agent's "running now" list.
        if (run.AgentId is { } agentId)
        {
            Publish(new LiveChange(LiveChangeKind.Agent, agentId));
        }
    }

    /// <summary>Tells listeners the set of installed pipelines changed.</summary>
    public void PipelinesChanged() => Publish(new LiveChange(LiveChangeKind.Pipelines, Guid.Empty));

    // ---- updates (called after the database was written) --------------------------------

    /// <summary>An agent was registered.</summary>
    public Task AgentRegisteredAsync(ServerDbContext db, Agent agent, CancellationToken cancellationToken) =>
        ApplyAsync(db, nameof(AgentRegisteredAsync), () =>
        {
            _agents[agent.Id] = LiveAgent.From(agent);
            Publish(new LiveChange(LiveChangeKind.Agent, agent.Id));
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>An agent heartbeated, so it is online.</summary>
    public Task AgentHeartbeatAsync(ServerDbContext db, Guid agentId, DateTime heartbeatAt, CancellationToken cancellationToken) =>
        ApplyAsync(db, nameof(AgentHeartbeatAsync), () =>
        {
            if (_agents.TryGetValue(agentId, out var agent))
            {
                _agents[agentId] = agent with { Status = "Online", LastHeartbeatAtUtc = DateTime.SpecifyKind(heartbeatAt, DateTimeKind.Utc) };
                Publish(new LiveChange(LiveChangeKind.Agent, agentId));
            }

            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>Agents stopped heartbeating and were marked offline.</summary>
    public Task AgentsOfflineAsync(ServerDbContext db, IEnumerable<Guid> agentIds, CancellationToken cancellationToken) =>
        ApplyAsync(db, nameof(AgentsOfflineAsync), () =>
        {
            foreach (var agentId in agentIds)
            {
                if (_agents.TryGetValue(agentId, out var agent))
                {
                    _agents[agentId] = agent with { Status = "Offline" };
                    Publish(new LiveChange(LiveChangeKind.Agent, agentId));
                }
            }

            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>A run was created (its row is in the database).</summary>
    public Task RunCreatedAsync(ServerDbContext db, Guid runId, CancellationToken cancellationToken) =>
        ApplyAsync(db, nameof(RunCreatedAsync), async () =>
        {
            var detail = await RunQueries.LoadDetailAsync(db, runId, cancellationToken).ConfigureAwait(false);
            if (detail is not null)
            {
                var run = LiveRun.From(detail);
                _runs[runId] = run;
                PublishRun(run);
            }
        }, cancellationToken);

    /// <summary>A run's status, times or row counts changed; <paramref name="run"/> is its saved row.</summary>
    public Task RunChangedAsync(ServerDbContext db, Run run, CancellationToken cancellationToken) =>
        ApplyAsync(db, nameof(RunChangedAsync), async () =>
        {
            var live = await GetOrLoadRunAsync(db, run.Id, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                return;
            }

            live.Apply(run, run.AgentId is { } agentId && _agents.TryGetValue(agentId, out var agent) ? agent.MachineName : null);
            PublishRun(live);

            if (!live.IsActive)
            {
                EvictExpired();
            }
        }, cancellationToken);

    /// <summary>A run completed a stage (its row is in the database).</summary>
    public Task StageCompletedAsync(ServerDbContext db, Guid runId, StageDto stage, CancellationToken cancellationToken) =>
        ApplyAsync(db, nameof(StageCompletedAsync), async () =>
        {
            var live = await GetOrLoadRunAsync(db, runId, cancellationToken).ConfigureAwait(false);
            if (live is not null)
            {
                live.AddStage(stage);
                PublishRun(live);
            }
        }, cancellationToken);

    /// <summary>An agent sampled the CPU and memory of a run's process (the sample is in the database).</summary>
    public Task ResourceSampledAsync(ServerDbContext db, Guid runId, ResourceSampleDto sample, CancellationToken cancellationToken) =>
        ApplyAsync(db, nameof(ResourceSampledAsync), async () =>
        {
            var live = await GetOrLoadRunAsync(db, runId, cancellationToken).ConfigureAwait(false);
            if (live is not null)
            {
                live.AddSample(sample);
                Publish(new LiveChange(LiveChangeKind.Run, runId));
            }
        }, cancellationToken);

    // ---- reads -------------------------------------------------------------------------

    /// <summary>The runs in flight, newest first, from memory.</summary>
    public async Task<IReadOnlyList<RunSummaryDto>> ActiveRunsAsync(
        ServerDbContext db, Guid? pipelineId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);
        return ActiveRunSummaries(pipelineId);
    }

    /// <summary>One run with its stages and samples: from memory if it is held, otherwise from the database.</summary>
    public async Task<RunDetailDto?> GetRunAsync(ServerDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);
        return TryGetRun(runId) ?? await RunQueries.LoadDetailAsync(db, runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One run's summary, if it is held in memory.</summary>
    public RunSummaryDto? TryGetRunSummary(Guid runId) => _runs.TryGetValue(runId, out var run) ? run.ToSummary() : null;

    /// <summary>One run with its stages and samples, if it is held in memory.</summary>
    public RunDetailDto? TryGetRun(Guid runId) => _runs.TryGetValue(runId, out var run) ? run.ToDetail() : null;

    /// <summary>How many runs of <paramref name="pipelineId"/> are in flight.</summary>
    public async Task<int> ActiveRunCountAsync(ServerDbContext db, Guid pipelineId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);
        return _runs.Values.Count(run => run.PipelineId == pipelineId && run.IsActive);
    }

    /// <summary>
    /// The latest run of <paramref name="pipelineId"/>: from memory when it has one in flight or
    /// recent, otherwise from the database.
    /// </summary>
    public async Task<RunSummaryDto?> LastRunAsync(ServerDbContext db, Guid pipelineId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);

        var held = _runs.Values
            .Where(run => run.PipelineId == pipelineId)
            .Select(run => run.ToSummary())
            .OrderByDescending(run => run.RequestedAtUtc)
            .FirstOrDefault();

        return held ?? await RunQueries.LastRunAsync(db, pipelineId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every agent, by machine name, from memory.</summary>
    public async Task<IReadOnlyList<AgentDto>> ListAgentsAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        return _agents.Values
            .OrderBy(agent => agent.MachineName, StringComparer.Ordinal)
            .ThenBy(agent => agent.LastHeartbeatAtUtc)
            .Select(agent => agent.ToDto(now, ActiveRunsOf(agent.Id)))
            .ToList();
    }

    /// <summary>One agent, if it is held in memory.</summary>
    public AgentDto? TryGetAgent(Guid agentId) =>
        _agents.TryGetValue(agentId, out var agent) ? agent.ToDto(timeProvider.GetUtcNow(), ActiveRunsOf(agentId)) : null;

    private List<RunSummaryDto> ActiveRunSummaries(Guid? pipelineId) =>
        _runs.Values
            .Where(run => run.IsActive && (pipelineId is null || run.PipelineId == pipelineId))
            .Select(run => run.ToSummary())
            .OrderByDescending(run => run.RequestedAtUtc)
            .ToList();

    private List<RunSummaryDto> ActiveRunsOf(Guid agentId) =>
        _runs.Values
            .Where(run => run.IsActive && run.AgentId == agentId)
            .Select(run => run.ToSummary())
            .OrderByDescending(run => run.RequestedAtUtc)
            .ToList();

    // ---- loading -----------------------------------------------------------------------

    /// <summary>Loads the state from the database if it has not been loaded yet (or an update failed since).</summary>
    public async Task EnsureLoadedAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            await LoadAsync(db, cancellationToken).ConfigureAwait(false);
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task LoadAsync(ServerDbContext db, CancellationToken cancellationToken)
    {
        var agents = new ConcurrentDictionary<Guid, LiveAgent>();
        foreach (var agent in await db.Agents.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
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

        _agents = agents;
        _runs = runs;
        logger.LogInformation("Loaded {AgentCount} agent(s) and {RunCount} run(s) into the live state.", agents.Count, runs.Count);
    }

    private async Task<LiveRun?> GetOrLoadRunAsync(ServerDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        if (_runs.TryGetValue(runId, out var held))
        {
            return held;
        }

        // Not held: it finished before the retention window, or the update is for a run created
        // by something that never told this class. Read it - the database already has this update.
        var detail = await RunQueries.LoadDetailAsync(db, runId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        return _runs.GetOrAdd(runId, _ => LiveRun.From(detail));
    }

    /// <summary>
    /// Runs an update. It loads the state first if need be, and never throws: the call that
    /// triggered it has already written the database, so a failure here only marks the state to be
    /// rebuilt from the database the next time it is used.
    /// </summary>
    private async Task ApplyAsync(ServerDbContext db, string update, Func<Task> apply, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureLoadedAsync(db, cancellationToken).ConfigureAwait(false);
            await apply().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Live state update {Update} failed; the state will be reloaded from the database.", update);
            _loaded = false;
        }
    }

    private void EvictExpired()
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - Retention;
        foreach (var (runId, run) in _runs)
        {
            if (!run.IsActive && run.CompletedAtUtc < cutoff)
            {
                _runs.TryRemove(runId, out _);
            }
        }
    }
}
