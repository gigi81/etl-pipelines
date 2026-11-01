using System.Net.ServerSentEvents;
using EtlPipelines.Server.Agents;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Runs;
using Grpc.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Api;

/// <summary>
/// The REST surface the WebUI (<c>web/</c>) talks to. A browser cannot call the gRPC services
/// (they are served over cleartext HTTP/2 only), so this exposes the same data - and the same
/// "run a pipeline" action - as minimal APIs on the server's HTTP/1.1 endpoint.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>Maps every <c>/api</c> route.</summary>
    public static IEndpointRouteBuilder MapEtlApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/pipelines", ListPipelinesAsync);
        api.MapPost("/pipelines/{pipelineId:guid}/runs", TriggerRunAsync);
        api.MapGet("/runs", ListRunsAsync);
        api.MapGet("/runs/{runId:guid}", GetRunAsync);
        api.MapGet("/runs/{runId:guid}/stream", StreamRunAsync);
        api.MapGet("/events", StreamEvents);
        api.MapGet("/agents", ListAgentsAsync);
        api.MapPut("/agents/{agentId:guid}/tags", SetAgentTagsAsync);
        api.MapDelete("/agents/{agentId:guid}", RemoveAgentAsync);
        PipelineEndpoints.Map(api);
        PackageEndpoints.Map(api);
        ConfigurationEndpoints.Map(api);

        // An unknown /api path is a 404, not the UI's index.html the SPA fallback would otherwise serve.
        api.MapFallback(() => Results.NotFound());

        return app;
    }

    private static async Task<Ok<List<PipelineDto>>> ListPipelinesAsync(
        ServerDbContext db, PackageCatalogService catalog, LiveState live, CancellationToken cancellationToken)
    {
        // The catalog is the database's (it only changes when a package is installed or removed, and
        // clients are told when it does); how each pipeline is doing is the live state's.
        var pipelines = await catalog.ListInstalledPipelinesAsync(cancellationToken).ConfigureAwait(false);

        // The names users gave pipelines, by (package, the pipeline's own name).
        var displayNames = (await db.PipelineDisplayNames
                .AsNoTracking()
                .Select(entry => new { entry.Package.NugetPackageId, entry.PipelineName, entry.DisplayName })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(entry => (entry.NugetPackageId, entry.PipelineName), entry => entry.DisplayName);

        // Where each pipeline may run - leaving out agents that are gone, which are never chosen anyway.
        var assignments = await PipelineAssignmentStore.LoadAllAsync(db, cancellationToken).ConfigureAwait(false);

        var result = new List<PipelineDto>(pipelines.Count);
        foreach (var pipeline in pipelines)
        {
            result.Add(new PipelineDto(
                pipeline.PipelineId,
                pipeline.Name,
                displayNames.GetValueOrDefault((pipeline.PackageId, pipeline.Name)),
                pipeline.PackageId,
                pipeline.PackageVersion,
                await live.ActiveRunCountAsync(pipeline.PipelineId, cancellationToken).ConfigureAwait(false),
                await live.LastRunAsync(db, pipeline.PipelineId, cancellationToken).ConfigureAwait(false),
                ToDto(assignments.GetValueOrDefault((pipeline.PackageId, pipeline.Name)) ?? PipelineAssignment.Any, live)));
        }

        return TypedResults.Ok(result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>The assignment as the API shows it: only the agents that exist.</summary>
    internal static PipelineAssignmentDto ToDto(PipelineAssignment assignment, LiveState live) =>
        new(assignment.Tags, [.. assignment.AgentIds.Where(live.IsKnownAgent)]);

    private static async Task<Results<Accepted<TriggerRunResponse>, ProblemHttpResult>> TriggerRunAsync(
        Guid pipelineId, RunDispatcher dispatcher, CancellationToken cancellationToken)
    {
        try
        {
            var runId = await dispatcher.DispatchAsync(pipelineId, cancellationToken).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/runs/{runId}", new TriggerRunResponse(runId));
        }
        catch (RpcException exception)
        {
            return ApiProblems.From(exception);
        }
    }

    private static async Task<Ok<IReadOnlyList<RunSummaryDto>>> ListRunsAsync(
        ServerDbContext db, LiveState live, CancellationToken cancellationToken, bool? active = null, Guid? pipelineId = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        // Runs in flight come from memory alone. Finished ones are memory's recent ones merged with the
        // database's older ones, read when asked for - the database can be a few seconds behind, so a
        // run that has only just finished is only in memory.
        return TypedResults.Ok(await live.ListRunsAsync(db, active, pipelineId, limit, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<RunDetailDto>, NotFound>> GetRunAsync(
        Guid runId, ServerDbContext db, LiveState live, CancellationToken cancellationToken)
    {
        var detail = await live.GetRunAsync(db, runId, cancellationToken).ConfigureAwait(false);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Ok<IReadOnlyList<AgentDto>>> ListAgentsAsync(LiveState live, CancellationToken cancellationToken) =>
        TypedResults.Ok(await live.ListAgentsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Gives an agent tags (replacing the ones a user gave it before), which pipelines can be assigned by.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> SetAgentTagsAsync(
        Guid agentId, AgentTagsDto body, LiveState live, CancellationToken cancellationToken)
    {
        if (!TagNames.TryNormalize(body.Tags, out var tags, out var error))
        {
            return TypedResults.Problem(detail: error, statusCode: StatusCodes.Status400BadRequest);
        }

        return await live.SetAgentTagsAsync(agentId, tags, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : TypedResults.Problem(detail: $"No agent '{agentId}' is known to this server.", statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>Removes an agent that has stopped heartbeating, so it is no longer listed. Its past runs keep its name.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAgentAsync(
        Guid agentId, LiveState live, AgentConnectionRegistry connections, CancellationToken cancellationToken)
    {
        switch (await live.RemoveAgentAsync(agentId, cancellationToken).ConfigureAwait(false))
        {
            case AgentRemovalResult.Removed:
                // Nothing more is to be sent its way, whether or not its process is somehow still holding a stream open.
                connections.Disconnect(agentId.ToString());
                return TypedResults.NoContent();

            case AgentRemovalResult.StillOnline:
                return TypedResults.Problem(
                    detail: "That agent is online. Only an agent that has stopped heartbeating can be removed.",
                    statusCode: StatusCodes.Status409Conflict);

            case AgentRemovalResult.HasActiveRuns:
                return TypedResults.Problem(detail: "That agent still has runs in progress.", statusCode: StatusCodes.Status409Conflict);

            default:
                return TypedResults.Problem(detail: $"No agent '{agentId}' is known to this server.", statusCode: StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// Follows one run as server-sent events: its full detail right away, then again each time a
    /// stage completes, a resource sample arrives or the status changes, ending once the run has
    /// finished. See <see cref="LiveStreams"/>.
    /// </summary>
    private static async Task<Results<ServerSentEventsResult<string>, NotFound>> StreamRunAsync(
        Guid runId, ServerDbContext db, LiveState live, HttpContext http)
    {
        // Listen before reading the run, so a change in between is not missed.
        var subscription = live.Subscribe(change => change is { Kind: LiveChangeKind.Run } && change.Id == runId);
        http.RequestAborted.Register(subscription.Dispose);

        var detail = await live.GetRunAsync(db, runId, http.RequestAborted).ConfigureAwait(false);
        if (detail is null)
        {
            subscription.Dispose();
            return TypedResults.NotFound();
        }

        return TypedResults.ServerSentEvents(LiveStreams.RunAsync(live, subscription, runId, detail, http.RequestAborted));
    }

    /// <summary>The change feed for the lists. See <see cref="LiveStreams.EventsAsync"/>.</summary>
    private static ServerSentEventsResult<string> StreamEvents(LiveState live, HttpContext http)
    {
        // Subscribed here, before the response starts, so nothing that happens once the client sees the
        // stream open can be missed.
        var subscription = live.Subscribe();
        http.RequestAborted.Register(subscription.Dispose);

        return TypedResults.ServerSentEvents(LiveStreams.EventsAsync(live, subscription, http.RequestAborted));
    }
}
