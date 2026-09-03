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
/// dispatches. A run's buffered history is retained for this server process's whole lifetime once
/// it starts (there is no eviction here yet) - acceptable for this phase's scope, since it is
/// bounded by how many runs a process actually executes, not by anything unbounded within a
/// single run; a real cache/eviction policy is the same kind of follow-up Phase 8's cache eviction
/// work already covers for the agent/server disk caches. Registered as a singleton: every gRPC
/// call into <see cref="Services.PipelineExecutionServiceImpl"/>/<see cref="Services.ManagementServiceImpl"/>
/// shares the one instance, since a run's history and its live subscribers are process-wide state.
/// </remarks>
public sealed class RunStatusStore
{
    private readonly ConcurrentDictionary<Guid, RunState> _runs = new();

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

                foreach (var subscriber in state.Subscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                state.Subscribers.Clear();
            }
        }
    }

    private sealed class RunState
    {
        public List<RunProgressEvent> History { get; } = [];

        public List<Channel<RunProgressEvent>> Subscribers { get; } = [];

        public bool Completed { get; set; }
    }
}
