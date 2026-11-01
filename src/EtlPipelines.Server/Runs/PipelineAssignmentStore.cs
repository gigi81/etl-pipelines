using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Runs;

/// <summary>
/// Reads and writes where each pipeline may run (<c>PipelineAgentTags</c>, <c>PipelineAgents</c>). Kept per
/// (package, pipeline name), not per installed pipeline row, so it survives the package being updated.
/// </summary>
public static class PipelineAssignmentStore
{
    /// <summary>Where the pipeline called <paramref name="pipelineName"/> of the package <paramref name="packageId"/> may run.</summary>
    public static async Task<PipelineAssignment> LoadAsync(
        ServerDbContext db, Guid packageId, string pipelineName, CancellationToken cancellationToken)
    {
        var tags = await db.PipelineAgentTags
            .AsNoTracking()
            .Where(row => row.PackageId == packageId && row.PipelineName == pipelineName)
            .Select(row => row.Tag)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var agentIds = await db.PipelineAgents
            .AsNoTracking()
            .Where(row => row.PackageId == packageId && row.PipelineName == pipelineName)
            .Select(row => row.AgentId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PipelineAssignment([.. tags.Order(StringComparer.Ordinal)], [.. agentIds.Order()]);
    }

    /// <summary>Every assignment, by NuGet package id and pipeline name. Pipelines with none are not in it.</summary>
    public static async Task<Dictionary<(string PackageId, string PipelineName), PipelineAssignment>> LoadAllAsync(
        ServerDbContext db, CancellationToken cancellationToken)
    {
        var tags = await db.PipelineAgentTags
            .AsNoTracking()
            .Select(row => new { row.Package.NugetPackageId, row.PipelineName, row.Tag })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var agents = await db.PipelineAgents
            .AsNoTracking()
            .Select(row => new { row.Package.NugetPackageId, row.PipelineName, row.AgentId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return tags.Select(row => (row.NugetPackageId, row.PipelineName))
            .Concat(agents.Select(row => (row.NugetPackageId, row.PipelineName)))
            .Distinct()
            .ToDictionary(
                key => key,
                key => new PipelineAssignment(
                    [.. tags.Where(row => (row.NugetPackageId, row.PipelineName) == key).Select(row => row.Tag).Order(StringComparer.Ordinal)],
                    [.. agents.Where(row => (row.NugetPackageId, row.PipelineName) == key).Select(row => row.AgentId).Order()]));
    }

    /// <summary>Replaces where the pipeline may run with <paramref name="assignment"/>; an assignment of nothing means anywhere.</summary>
    public static async Task SaveAsync(
        ServerDbContext db, Guid packageId, string pipelineName, PipelineAssignment assignment, CancellationToken cancellationToken)
    {
        // Only what differs is written: a row removed and added back under the same key would be two
        // tracked entities with one key.
        var currentTags = await db.PipelineAgentTags
            .Where(row => row.PackageId == packageId && row.PipelineName == pipelineName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.PipelineAgentTags.RemoveRange(currentTags.Where(row => !assignment.Tags.Contains(row.Tag)));
        db.PipelineAgentTags.AddRange(
            assignment.Tags
                .Where(tag => currentTags.All(row => row.Tag != tag))
                .Select(tag => new PipelineAgentTag { PackageId = packageId, PipelineName = pipelineName, Tag = tag }));

        var currentAgents = await db.PipelineAgents
            .Where(row => row.PackageId == packageId && row.PipelineName == pipelineName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.PipelineAgents.RemoveRange(currentAgents.Where(row => !assignment.AgentIds.Contains(row.AgentId)));
        db.PipelineAgents.AddRange(
            assignment.AgentIds
                .Where(id => currentAgents.All(row => row.AgentId != id))
                .Select(id => new PipelineAgent { PackageId = packageId, PipelineName = pipelineName, AgentId = id }));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
