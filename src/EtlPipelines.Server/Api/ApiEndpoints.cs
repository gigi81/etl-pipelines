using System.Net.ServerSentEvents;
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

        var result = new List<PipelineDto>(pipelines.Count);
        foreach (var pipeline in pipelines)
        {
            result.Add(new PipelineDto(
                pipeline.PipelineId,
                pipeline.Name,
                pipeline.PackageId,
                pipeline.PackageVersion,
                await live.ActiveRunCountAsync(db, pipeline.PipelineId, cancellationToken).ConfigureAwait(false),
                await live.LastRunAsync(db, pipeline.PipelineId, cancellationToken).ConfigureAwait(false)));
        }

        return TypedResults.Ok(result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

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
            var status = exception.StatusCode switch
            {
                StatusCode.NotFound => StatusCodes.Status404NotFound,
                StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
                StatusCode.FailedPrecondition => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status500InternalServerError,
            };

            return TypedResults.Problem(detail: exception.Status.Detail, statusCode: status);
        }
    }

    private static async Task<Ok<IReadOnlyList<RunSummaryDto>>> ListRunsAsync(
        ServerDbContext db, LiveState live, CancellationToken cancellationToken, bool? active = null, Guid? pipelineId = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

        // Runs in flight are the live state's; history (and the unfiltered list) is the database's,
        // read when asked for.
        IReadOnlyList<RunSummaryDto> runs = active is true
            ? (await live.ActiveRunsAsync(db, pipelineId, cancellationToken).ConfigureAwait(false)).Take(limit).ToList()
            : await RunQueries.ListAsync(db, active, pipelineId, limit, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(runs);
    }

    private static async Task<Results<Ok<RunDetailDto>, NotFound>> GetRunAsync(
        Guid runId, ServerDbContext db, LiveState live, CancellationToken cancellationToken)
    {
        var detail = await live.GetRunAsync(db, runId, cancellationToken).ConfigureAwait(false);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Ok<IReadOnlyList<AgentDto>>> ListAgentsAsync(
        ServerDbContext db, LiveState live, CancellationToken cancellationToken) =>
        TypedResults.Ok(await live.ListAgentsAsync(db, cancellationToken).ConfigureAwait(false));

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
