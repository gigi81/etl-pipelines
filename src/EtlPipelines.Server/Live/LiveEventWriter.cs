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
        var created = events.OfType<RunCreatedEvent>().Select(e => e.RunId).ToHashSet();

        var agentIds = events
            .Select(e => e switch
            {
                // A registration is for an agent the database may already have: one starting over.
                AgentRegisteredEvent g => g.AgentId,
                AgentHeartbeatEvent h => h.AgentId,
                AgentOfflineEvent o => o.AgentId,
                AgentRemovedEvent r => r.AgentId,
                AgentTagsChangedEvent t => t.AgentId,
                _ => Guid.Empty,
            })
            .Where(id => id != Guid.Empty)
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

        // One transaction for the whole batch: the saves between its events (below) and the set-based
        // deletes all commit together, or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var skipped = 0;
        foreach (var liveEvent in events)
        {
            switch (liveEvent)
            {
                case AgentRegisteredEvent e when agents.TryGetValue(e.AgentId, out var returning):
                    // The same agent starting over (or one that had been removed): the row is updated in
                    // place, so what a user gave it and the runs it carried out stay with it.
                    returning.MachineName = e.MachineName;
                    returning.Tags = [.. e.Tags];
                    returning.Version = e.Version;
                    returning.Status = "Online";
                    returning.LastHeartbeatAt = e.AtUtc;
                    returning.Info = e.InfoJson;
                    break;

                case AgentRegisteredEvent e:
                    var newAgent = new Agent
                    {
                        Id = e.AgentId,
                        MachineName = e.MachineName,
                        Tags = [.. e.Tags],
                        AssignedTags = [],
                        Version = e.Version,
                        Status = "Online",
                        LastHeartbeatAt = e.AtUtc,
                        Info = e.InfoJson,
                    };
                    db.Agents.Add(newAgent);
                    agents[e.AgentId] = newAgent;
                    break;

                case AgentTagsChangedEvent e when agents.TryGetValue(e.AgentId, out var tagged):
                    tagged.AssignedTags = [.. e.AssignedTags];
                    break;

                case AgentHeartbeatEvent e when agents.TryGetValue(e.AgentId, out var agent):
                    agent.Status = "Online";
                    agent.LastHeartbeatAt = e.AtUtc;
                    break;

                case AgentOfflineEvent e when agents.TryGetValue(e.AgentId, out var agent):
                    agent.Status = "Offline";
                    break;

                case AgentRemovedEvent e when agents.TryGetValue(e.AgentId, out var agent):
                    // Marked, not deleted: the runs it carried out still point at it. Whoever it was set up
                    // as is forgotten with it.
                    agent.Status = "Removed";
                    agent.AssignedTags = [];
                    break;

                case PipelineHistoryClearedEvent e:
                    await ClearHistoryAsync(db, e.PipelineId, runs, stages, cancellationToken).ConfigureAwait(false);
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
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return skipped;
    }

    /// <summary>
    /// Deletes a pipeline's finished runs and what hangs off them. The changes made by the events before
    /// this one are saved first, so a run that finished earlier in the same batch is finished in the
    /// database when the delete looks for it; the runs deleted are then forgotten, so a later event
    /// cannot try to change a row that is gone.
    /// </summary>
    private static async Task ClearHistoryAsync(
        ServerDbContext db,
        Guid pipelineId,
        Dictionary<Guid, Run> runs,
        Dictionary<(Guid RunId, int Sequence), StageResult> stages,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var finished = RunQueries.TerminalStatuses;
        await db.AgentResourceSamples
            .Where(sample => sample.Run.PipelineId == pipelineId && finished.Contains(sample.Run.Status))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.StageResults
            .Where(stage => stage.Run.PipelineId == pipelineId && finished.Contains(stage.Run.Status))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Runs
            .Where(run => run.PipelineId == pipelineId && finished.Contains(run.Status))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (runId, run) in runs.Where(held => held.Value.PipelineId == pipelineId && finished.Contains(held.Value.Status)).ToList())
        {
            db.Entry(run).State = EntityState.Detached;
            runs.Remove(runId);
        }

        foreach (var key in stages.Keys.Where(key => !runs.ContainsKey(key.RunId)).ToList())
        {
            db.Entry(stages[key]).State = EntityState.Detached;
            stages.Remove(key);
        }
    }
}
