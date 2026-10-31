using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Live.Events;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Live;

/// <summary>
/// Replays <see cref="LiveEvent"/>s, in order, into the database tables - the same changes
/// <see cref="LiveState"/> made in memory, so the two agree. All the changes of a batch go in one
/// <c>SaveChanges</c>: one transaction, a handful of round trips however many events there are.
/// </summary>
/// <remarks>
/// Rows are loaded once per batch and then changed event by event, in order, so an entity that
/// several events touch (a run that starts, reports and finishes inside one batch) is written once,
/// with all of it. An event about something the database does not have (its row was never written,
/// or was dropped) is skipped rather than failing the batch.
/// </remarks>
public static class LiveEventWriter
{
    /// <summary>Writes <paramref name="events"/> to <paramref name="db"/> and saves. Returns how many were skipped.</summary>
    public static async Task<int> WriteAsync(ServerDbContext db, IReadOnlyList<LiveEvent> events, CancellationToken cancellationToken)
    {
        var registered = events.OfType<AgentRegisteredEvent>().Select(e => e.AgentId).ToHashSet();
        var created = events.OfType<RunCreatedEvent>().Select(e => e.RunId).ToHashSet();

        var agentIds = events
            .Select(e => e switch { AgentHeartbeatEvent h => h.AgentId, AgentOfflineEvent o => o.AgentId, _ => Guid.Empty })
            .Where(id => id != Guid.Empty && !registered.Contains(id))
            .Distinct()
            .ToList();
        var runIds = events
            .Select(e => e switch
            {
                RunStartedEvent s => s.RunId,
                RunFinishedEvent f => f.RunId,
                StageCompletedEvent c => c.RunId,
                _ => Guid.Empty,
            })
            .Where(id => id != Guid.Empty && !created.Contains(id))
            .Distinct()
            .ToList();
        var stageRunIds = events.OfType<StageCompletedEvent>().Select(e => e.RunId).Where(id => !created.Contains(id)).Distinct().ToList();

        var agents = agentIds.Count == 0
            ? []
            : await db.Agents.Where(agent => agentIds.Contains(agent.Id)).ToDictionaryAsync(agent => agent.Id, cancellationToken).ConfigureAwait(false);
        var runs = runIds.Count == 0
            ? []
            : await db.Runs.Where(run => runIds.Contains(run.Id)).ToDictionaryAsync(run => run.Id, cancellationToken).ConfigureAwait(false);
        var stages = stageRunIds.Count == 0
            ? []
            : await db.StageResults
                .Where(stage => stageRunIds.Contains(stage.RunId))
                .ToDictionaryAsync(stage => (stage.RunId, stage.Sequence), cancellationToken)
                .ConfigureAwait(false);

        var skipped = 0;
        foreach (var liveEvent in events)
        {
            switch (liveEvent)
            {
                case AgentRegisteredEvent e:
                    var newAgent = new Agent
                    {
                        Id = e.AgentId,
                        MachineName = e.MachineName,
                        Tags = [.. e.Tags],
                        Version = e.Version,
                        Status = "Online",
                        LastHeartbeatAt = e.AtUtc,
                        Info = e.InfoJson,
                    };
                    db.Agents.Add(newAgent);
                    agents[e.AgentId] = newAgent;
                    break;

                case AgentHeartbeatEvent e when agents.TryGetValue(e.AgentId, out var agent):
                    agent.Status = "Online";
                    agent.LastHeartbeatAt = e.AtUtc;
                    break;

                case AgentOfflineEvent e when agents.TryGetValue(e.AgentId, out var agent):
                    agent.Status = "Offline";
                    break;

                case RunCreatedEvent e:
                    var newRun = new Run
                    {
                        Id = e.RunId,
                        PipelineId = e.PipelineId,
                        AgentId = e.AgentId,
                        Status = "Dispatched",
                        RequestedAt = e.AtUtc,
                    };
                    db.Runs.Add(newRun);
                    runs[e.RunId] = newRun;
                    break;

                case RunStartedEvent e when runs.TryGetValue(e.RunId, out var run):
                    run.StartedAt ??= e.AtUtc;
                    if (run.Status is "Queued" or "Dispatched")
                    {
                        run.Status = "Running";
                    }

                    break;

                case RunFinishedEvent e when runs.TryGetValue(e.RunId, out var run):
                    run.Status = e.Status;
                    run.CompletedAt = e.AtUtc;
                    run.ExitCode = e.ExitCode ?? run.ExitCode;
                    run.RowsRead = e.RowsRead ?? run.RowsRead;
                    run.RowsWritten = e.RowsWritten ?? run.RowsWritten;
                    run.RowsFailed = e.RowsFailed ?? run.RowsFailed;
                    break;

                case StageCompletedEvent e when runs.ContainsKey(e.RunId):
                    // A stage reported twice for the same sequence replaces the first, as in memory.
                    if (!stages.TryGetValue((e.RunId, e.Stage.Sequence), out var stage))
                    {
                        stage = new StageResult { Id = e.StageId, RunId = e.RunId, Sequence = e.Stage.Sequence, Name = e.Stage.Name };
                        db.StageResults.Add(stage);
                        stages[(e.RunId, e.Stage.Sequence)] = stage;
                    }

                    stage.Name = e.Stage.Name;
                    stage.RowsIn = e.Stage.RowsIn;
                    stage.RowsOut = e.Stage.RowsOut;
                    stage.RowsFailed = e.Stage.RowsFailed;
                    stage.ElapsedMs = e.Stage.ElapsedMs;
                    stage.ErrorCode = e.Stage.ErrorCode;
                    stage.ErrorDescription = e.Stage.ErrorDescription;
                    break;

                case ResourceSampledEvent e when runs.ContainsKey(e.RunId) || created.Contains(e.RunId):
                    db.AgentResourceSamples.Add(new AgentResourceSample
                    {
                        Id = e.SampleId,
                        RunId = e.RunId,
                        AgentId = e.AgentId,
                        SampledAt = e.AtUtc,
                        CpuPercent = e.CpuPercent,
                        WorkingSetBytes = e.WorkingSetBytes,
                    });
                    break;

                default:
                    skipped++;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return skipped;
    }
}
