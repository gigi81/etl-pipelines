using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Api;

/// <summary>
/// What can be done to one pipeline, beyond running it: giving it a display name, choosing which agents may
/// run it, and clearing its history. (Listing and running are in <see cref="ApiEndpoints"/>.)
/// </summary>
internal static class PipelineEndpoints
{
    /// <summary>The longest display name accepted.</summary>
    internal const int MaxDisplayNameLength = 100;

    /// <summary>Maps the per-pipeline routes onto <paramref name="api"/>.</summary>
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPut("/pipelines/{pipelineId:guid}/display-name", SetDisplayNameAsync);
        api.MapPut("/pipelines/{pipelineId:guid}/assignment", SetAssignmentAsync);
        api.MapDelete("/pipelines/{pipelineId:guid}/runs", ClearHistoryAsync);
    }

    /// <summary>
    /// Sets how the UI labels a pipeline, or - given nothing - goes back to its own name. The pipeline's
    /// own name is what its package registers and what an agent launches it by, so it never changes; this
    /// is kept per (package, name), so it survives the package being updated.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetDisplayNameAsync(
        Guid pipelineId, PipelineDisplayNameDto body, ServerDbContext db, LiveState live, CancellationToken cancellationToken)
    {
        var pipeline = await db.Pipelines
            .AsNoTracking()
            .Where(candidate => candidate.Id == pipelineId)
            .Select(candidate => new { candidate.Name, candidate.PackageVersion.PackageId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (pipeline is null)
        {
            return TypedResults.Problem(detail: $"No pipeline '{pipelineId}' is known to this server.", statusCode: StatusCodes.Status404NotFound);
        }

        var displayName = body.DisplayName?.Trim();
        if (displayName?.Length > MaxDisplayNameLength)
        {
            return TypedResults.Problem(
                detail: $"A display name can be at most {MaxDisplayNameLength} characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await db.PipelineDisplayNames
            .SingleOrDefaultAsync(entry => entry.PackageId == pipeline.PackageId && entry.PipelineName == pipeline.Name, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(displayName))
        {
            if (existing is not null)
            {
                db.PipelineDisplayNames.Remove(existing);
            }
        }
        else if (existing is null)
        {
            db.PipelineDisplayNames.Add(new PipelineDisplayName
            {
                PackageId = pipeline.PackageId,
                PipelineName = pipeline.Name,
                DisplayName = displayName,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.DisplayName = displayName;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Everyone showing the pipelines list refetches it.
        live.PipelinesChanged();
        return TypedResults.NoContent();
    }

    /// <summary>
    /// Sets which agents may run a pipeline: those with any of the tags, and those named. Nothing at all
    /// means any agent. Kept per (package, name), so it survives the package being updated.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetAssignmentAsync(
        Guid pipelineId, PipelineAssignmentDto body, ServerDbContext db, LiveState live, CancellationToken cancellationToken)
    {
        var pipeline = await db.Pipelines
            .AsNoTracking()
            .Where(candidate => candidate.Id == pipelineId)
            .Select(candidate => new { candidate.Name, candidate.PackageVersion.PackageId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (pipeline is null)
        {
            return TypedResults.Problem(detail: $"No pipeline '{pipelineId}' is known to this server.", statusCode: StatusCodes.Status404NotFound);
        }

        if (!TagNames.TryNormalize(body.Tags, out var tags, out var error))
        {
            return TypedResults.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        }

        var agentIds = (body.AgentIds ?? []).Distinct().ToList();
        var unknown = agentIds.Where(id => !live.IsKnownAgent(id)).ToList();
        if (unknown.Count > 0)
        {
            return TypedResults.Problem(
                detail: $"No such agent: {string.Join(", ", unknown)}. It may have been removed.", statusCode: StatusCodes.Status400BadRequest);
        }

        await PipelineAssignmentStore.SaveAsync(db, pipeline.PackageId, pipeline.Name, new PipelineAssignment(tags, agentIds), cancellationToken)
            .ConfigureAwait(false);

        // Everyone showing the pipelines list refetches it.
        live.PipelinesChanged();
        return TypedResults.NoContent();
    }

    /// <summary>Deletes the pipeline's finished runs, with their stages and samples. Runs in flight are left alone.</summary>
    private static async Task<Results<Ok<ClearedHistoryDto>, ProblemHttpResult>> ClearHistoryAsync(
        Guid pipelineId, ServerDbContext db, LiveState live, CancellationToken cancellationToken)
    {
        if (!await db.Pipelines.AnyAsync(candidate => candidate.Id == pipelineId, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.Problem(detail: $"No pipeline '{pipelineId}' is known to this server.", statusCode: StatusCodes.Status404NotFound);
        }

        var finished = await live.ListRunsAsync(db, active: false, pipelineId, int.MaxValue, cancellationToken).ConfigureAwait(false);
        await live.ClearHistoryAsync(pipelineId, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new ClearedHistoryDto(finished.Count));
    }
}
