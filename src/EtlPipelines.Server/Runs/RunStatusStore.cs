using System.Collections.Concurrent;
using System.Threading.Channels;
using EtlPipelines.Management.V1;

namespace EtlPipelines.Server.Runs;

/// <summary>
/// Fans out <see cref="RunProgressEvent"/>s to whoever is calling <c>ManagementService.StreamRunProgress</c>
/// for a given run, and buffers each run's history so a subscriber that connects after some
/// stages already completed still sees all of them (SERVER.md Phase 6).
/// </summary>
/// <remarks>
/// In-memory only, deliberately - the same "nothing durable to keep here" reasoning
/// <see cref="Agents.AgentConnectionRegistry"/>'s own remarks give for connections and pending
/// dispatches. A completed run's history is evicted once it has been completed for longer than the
/// retention period (one hour by default), checked whenever another run completes - so the store
/// is bounded by how many runs complete within that window, not by the process's whole lifetime.
/// Everything evicted is already durable in <c>Runs</c>/<c>StageResults</c>, which is where
/// <see cref="Services.ManagementServiceImpl.StreamRunProgress"/> answers from for a run this
/// store no longer tracks (<see cref="IsTracked"/>). Registered as a singleton: every gRPC
/// call into <see cref="Services.PipelineExecutionServiceImpl"/>/<see cref="Services.ManagementServiceImpl"/>
/// shares the one instance, since a run's history and its live subscribers are process-wide state.
/// </remarks>
public sealed class RunStatusStore
{
    /// <summary>How long a completed run's history is kept by default.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;

    /// <summary>Real wall-clock time, <see cref="DefaultRetention"/> - what DI resolves.</summary>
    public RunStatusStore()
        : this(TimeProvider.System, DefaultRetention)
    {
    }

    /// <summary>For tests that need to drive eviction with a fake clock.</summary>
    public RunStatusStore(TimeProvider timeProvider, TimeSpan retention)
    {
        _timeProvider = timeProvider;
        _retention = retention;
    }

    /// <summary>Whether this store currently holds anything for <paramref name="runId"/> - false for a run never published or subscribed to, or one already evicted.</summary>
    public bool IsTracked(Guid runId) => _runs.ContainsKey(runId);

    /// <summary>Publishes that one stage of <paramref name="runId"/> completed.</summary>
    public void PublishStageCompleted(Guid runId, StageCompleted stageCompleted) =>
        Publish(runId, new RunProgressEvent { RunId = runId.ToString(), StageCompleted = stageCompleted }, terminal: false);

    /// <summary>Publishes that <paramref name="runId"/> itself completed - the last event this run will ever publish.</summary>
    public void PublishRunCompleted(Guid runId, RunCompleted runCompleted) =>
        Publish(runId, new RunProgressEvent { RunId = runId.ToString(), RunCompleted = runCompleted }, terminal: true);

    /// <summary>
    /// Subscribes to <paramref name="runId"/>'s progress - the returned reader first replays
    /// everything already published for this run, then carries whatever is published afterwards,
    /// completing once <see cref="PublishRunCompleted"/> is called (or immediately, already
    /// completed, if that already happened before this call).
    /// </summary>
    public ChannelReader<RunProgressEvent> Subscribe(Guid runId)
    {
        var channel = Channel.CreateUnbounded<RunProgressEvent>();
        var state = _runs.GetOrAdd(runId, static _ => new RunState());

        lock (state)
        {
            foreach (var published in state.History)
            {
                channel.Writer.TryWrite(published);
            }

            if (state.Completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                state.Subscribers.Add(channel);
            }
        }

        return channel.Reader;
    }

    private void Publish(Guid runId, RunProgressEvent progressEvent, bool terminal)
    {
        var state = _runs.GetOrAdd(runId, static _ => new RunState());

        lock (state)
        {
            state.History.Add(progressEvent);

            foreach (var subscriber in state.Subscribers)
            {
                subscriber.Writer.TryWrite(progressEvent);
            }

            if (terminal)
            {
                state.Completed = true;
                state.CompletedAt = _timeProvider.GetUtcNow();

                foreach (var subscriber in state.Subscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                state.Subscribers.Clear();
            }
        }

        if (terminal)
        {
            EvictExpired();
        }
    }

    private void EvictExpired()
    {
        var cutoff = _timeProvider.GetUtcNow() - _retention;

        foreach (var (runId, state) in _runs)
        {
            lock (state)
            {
                // A completed state never gains subscribers again (Subscribe completes the
                // reader immediately instead), so removing it can never strand a live one.
                if (state.Completed && state.CompletedAt < cutoff)
                {
                    _runs.TryRemove(runId, out _);
                }
            }
        }
    }

    private sealed class RunState
    {
        public List<RunProgressEvent> History { get; } = [];

        public List<Channel<RunProgressEvent>> Subscribers { get; } = [];

        public bool Completed { get; set; }

        public DateTimeOffset CompletedAt { get; set; }
    }
}
