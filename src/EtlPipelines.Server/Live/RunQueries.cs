using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Live;

/// <summary>
/// The database reads behind the live view: what <see cref="LiveState"/> loads itself from (on first
/// use, and again after a failed update), and the history the in-memory state does not hold - runs
/// that finished before its retention window, which the UI lists on demand.
/// </summary>
public static class RunQueries
{
    /// <summary>The statuses of a run that has not finished.</summary>
    public static readonly List<string> ActiveStatuses = ["Queued", "Dispatched", "Running"];

    /// <summary>The statuses of a run that has finished.</summary>
    public static readonly List<string> TerminalStatuses = ["Succeeded", "Failed", "AgentLost"];

    /// <summary>Whether a run with <paramref name="status"/> is still in flight.</summary>
    public static bool IsActive(string status) => ActiveStatuses.Contains(status);

    /// <summary>
    /// Projects <c>Runs</c> onto <see cref="RunSummaryDto"/>, one SQL query, no tracking. Filter,
    /// order and page <paramref name="runs"/> first - EF cannot translate a filter or ordering over the
    /// projected DTO.
    /// </summary>
    public static IQueryable<RunSummaryDto> Project(IQueryable<Run> runs) =>
        runs
            .AsNoTracking()
            .Select(r => new RunSummaryDto(
                r.Id,
                r.PipelineId,
                r.Pipeline.Name,
                r.Pipeline.PackageVersion.Package.NugetPackageId,
                r.Pipeline.PackageVersion.Version,
                r.AgentId,
                r.Agent != null ? r.Agent.MachineName : null,
                r.Status,
                r.RequestedAt,
                r.StartedAt,
                r.CompletedAt,
                r.ExitCode,
                r.RowsRead,
                r.RowsWritten,
                r.RowsFailed,
                r.StageResults.Count,
                null));

    /// <summary>Runs from the database, newest first - the history the in-memory state does not hold.</summary>
    public static async Task<List<RunSummaryDto>> ListAsync(
        ServerDbContext db, bool? active, Guid? pipelineId, int limit, CancellationToken cancellationToken)
    {
        IQueryable<Run> query = db.Runs;
        if (active is true)
        {
            query = query.Where(r => ActiveStatuses.Contains(r.Status));
        }
        else if (active is false)
        {
            query = query.Where(r => TerminalStatuses.Contains(r.Status));
        }

        if (pipelineId is { } id)
        {
            query = query.Where(r => r.PipelineId == id);
        }

        return await Project(query.OrderByDescending(r => r.RequestedAt).Take(limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The most recently requested run of <paramref name="pipelineId"/>, if it has ever run.</summary>
    public static Task<RunSummaryDto?> LastRunAsync(ServerDbContext db, Guid pipelineId, CancellationToken cancellationToken) =>
        Project(db.Runs.Where(r => r.PipelineId == pipelineId).OrderByDescending(r => r.RequestedAt).Take(1))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The stage names of <paramref name="pipelineId"/>'s most recent successful run, in order.</summary>
    public static async Task<List<string>> ExpectedStageNamesAsync(
        ServerDbContext db, Guid pipelineId, Guid? excludeRunId, CancellationToken cancellationToken)
    {
        var lastSucceeded = await db.Runs
            .AsNoTracking()
            .Where(r => r.PipelineId == pipelineId && r.Status == "Succeeded" && r.Id != excludeRunId)
            .OrderByDescending(r => r.CompletedAt)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (lastSucceeded is null)
        {
            return [];
        }

        return await db.StageResults
            .AsNoTracking()
            .Where(s => s.RunId == lastSucceeded)
            .OrderBy(s => s.Sequence)
            .Select(s => s.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>One run with everything the run page shows, read from the database.</summary>
    public static async Task<RunDetailDto?> LoadDetailAsync(ServerDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var run = await Project(db.Runs.Where(r => r.Id == runId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (run is null)
        {
            return null;
        }

        var stages = await db.StageResults
            .AsNoTracking()
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.Sequence)
            .Select(s => new StageDto(s.Sequence, s.Name, s.RowsIn, s.RowsOut, s.RowsFailed, s.ElapsedMs, s.ErrorCode, s.ErrorDescription))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var samples = await db.AgentResourceSamples
            .AsNoTracking()
            .Where(s => s.RunId == runId)
            .OrderByDescending(s => s.SampledAt)
            .Take(LiveRun.MaxSamples)
            .Select(s => new ResourceSampleDto(s.SampledAt, s.CpuPercent, s.WorkingSetBytes))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        samples.Reverse();

        var expected = await ExpectedStageNamesAsync(db, run.PipelineId, runId, cancellationToken).ConfigureAwait(false);

        return new RunDetailDto(
            run with { StagesExpected = expected.Count > 0 ? expected.Count : null },
            stages,
            expected,
            samples);
    }
}
