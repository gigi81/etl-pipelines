using System.Runtime.CompilerServices;
using System.Text.Json;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
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
    /// <summary>Runs that have not finished yet.</summary>
    private static readonly List<string> ActiveStatuses = ["Queued", "Dispatched", "Running"];

    private static readonly List<string> TerminalStatuses = ["Succeeded", "Failed", "AgentLost"];

    /// <summary>How often the live stream looks for changes to the run it is following.</summary>
    internal static readonly TimeSpan StreamPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long the live stream goes without sending anything before it repeats the last snapshot, to keep proxies from closing it.</summary>
    internal static readonly TimeSpan StreamKeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>Maps every <c>/api</c> route.</summary>
    public static IEndpointRouteBuilder MapEtlApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/pipelines", ListPipelinesAsync);
        api.MapPost("/pipelines/{pipelineId:guid}/runs", TriggerRunAsync);
        api.MapGet("/runs", ListRunsAsync);
        api.MapGet("/runs/{runId:guid}", GetRunAsync);
        api.MapGet("/runs/{runId:guid}/stream", StreamRunAsync);
        api.MapGet("/agents", ListAgentsAsync);

        // An unknown /api path is a 404, not the UI's index.html the SPA fallback would otherwise serve.
        api.MapFallback(() => Results.NotFound());

        return app;
    }

    private static async Task<Ok<List<PipelineDto>>> ListPipelinesAsync(
        ServerDbContext db, PackageCatalogService catalog, CancellationToken cancellationToken)
    {
        var pipelines = await catalog.ListInstalledPipelinesAsync(cancellationToken).ConfigureAwait(false);
        var ids = pipelines.Select(p => p.PipelineId).ToList();

        var activeCounts = await db.Runs
            .AsNoTracking()
            .Where(r => ids.Contains(r.PipelineId) && ActiveStatuses.Contains(r.Status))
            .GroupBy(r => r.PipelineId)
            .Select(g => new { PipelineId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PipelineId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        // The most recently requested run per pipeline. Done per pipeline rather than as one
        // grouped query: it stays portable across providers, and the number of installed
        // pipelines is small.
        var result = new List<PipelineDto>(pipelines.Count);
        foreach (var pipeline in pipelines)
        {
            var lastRun = await Project(db.Runs
                    .Where(r => r.PipelineId == pipeline.PipelineId)
                    .OrderByDescending(r => r.RequestedAt)
                    .Take(1))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            result.Add(new PipelineDto(
                pipeline.PipelineId,
                pipeline.Name,
                pipeline.PackageId,
                pipeline.PackageVersion,
                activeCounts.GetValueOrDefault(pipeline.PipelineId),
                lastRun));
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

    private static async Task<Ok<List<RunSummaryDto>>> ListRunsAsync(
        ServerDbContext db, CancellationToken cancellationToken, bool? active = null, Guid? pipelineId = null, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);

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

        var runs = await Project(query.OrderByDescending(r => r.RequestedAt).Take(limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(await WithExpectedStagesAsync(db, runs, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<RunDetailDto>, NotFound>> GetRunAsync(
        Guid runId, ServerDbContext db, CancellationToken cancellationToken)
    {
        var detail = await LoadRunDetailAsync(db, runId, cancellationToken).ConfigureAwait(false);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    /// <summary>
    /// Follows one run as server-sent events: the run's full detail right away, then again whenever
    /// a stage completes, a resource sample arrives or the status changes (and every
    /// <see cref="StreamKeepAlive"/> otherwise), ending once the run has finished.
    /// </summary>
    /// <remarks>
    /// Reads <c>Runs</c>/<c>StageResults</c>/<c>AgentResourceSamples</c> - which the pipeline process
    /// and its agent write as they report in - rather than subscribing to <c>RunStatusStore</c>:
    /// that store carries stage events only, not the resource samples, and a subscriber that
    /// disconnects is never removed from it.
    /// </remarks>
    private static async Task<Results<ServerSentEventsResult<RunDetailDto>, NotFound>> StreamRunAsync(
        Guid runId, ServerDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        // An unknown run is a plain 404 rather than a stream that ends before it starts.
        if (await LoadRunDetailAsync(db, runId, cancellationToken).ConfigureAwait(false) is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.ServerSentEvents(FollowRunAsync(db, time, runId, cancellationToken), eventType: "run");
    }

    internal static async IAsyncEnumerable<RunDetailDto> FollowRunAsync(
        ServerDbContext db, TimeProvider time, Guid runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? lastFingerprint = null;
        var lastSent = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            var detail = await LoadRunDetailAsync(db, runId, cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                yield break;
            }

            var fingerprint = Fingerprint(detail);
            var now = time.GetUtcNow();
            if (fingerprint != lastFingerprint || now - lastSent >= StreamKeepAlive)
            {
                lastFingerprint = fingerprint;
                lastSent = now;
                yield return detail;
            }

            if (TerminalStatuses.Contains(detail.Run.Status))
            {
                yield break;
            }

            try
            {
                await Task.Delay(StreamPollInterval, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private static string Fingerprint(RunDetailDto detail) =>
        $"{detail.Run.Status}|{detail.Run.AgentId}|{detail.Stages.Count}|{detail.ResourceSamples.LastOrDefault()?.SampledAtUtc:O}|{detail.Run.CompletedAtUtc:O}";

    private static async Task<Ok<List<AgentDto>>> ListAgentsAsync(ServerDbContext db, TimeProvider time, CancellationToken cancellationToken)
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.MachineName)
            .ThenBy(a => a.LastHeartbeatAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var activeRuns = await WithExpectedStagesAsync(
            db,
            await Project(db.Runs
                    .Where(r => r.AgentId != null && ActiveStatuses.Contains(r.Status))
                    .OrderByDescending(r => r.RequestedAt))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        var now = time.GetUtcNow();
        var result = agents.Select(agent =>
        {
            var info = ParseInfo(agent.Info);
            return new AgentDto(
                agent.Id,
                agent.MachineName,
                agent.Tags,
                agent.Version,
                agent.Status,
                agent.LastHeartbeatAt,
                info,
                agent.Status == "Online" ? UptimeSeconds(info, now) : null,
                activeRuns.Where(r => r.AgentId == agent.Id).ToList());
        }).ToList();

        return TypedResults.Ok(result);
    }

    /// <summary>The stored <c>Agents.Info</c> as JSON; <see langword="null"/> when there is none, or it is not a JSON object.</summary>
    private static JsonElement? ParseInfo(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Seconds from <c>info.startedAtUtc</c> to <paramref name="now"/>, when the agent reported a start time.</summary>
    private static long? UptimeSeconds(JsonElement? info, DateTimeOffset now) =>
        info is { } element
            && element.TryGetProperty("startedAtUtc", out var startedAt)
            && startedAt.ValueKind == JsonValueKind.String
            && startedAt.TryGetDateTimeOffset(out var started)
                ? Math.Max(0, (long)(now - started).TotalSeconds)
                : null;

    /// <summary>Projects <c>Runs</c> onto <see cref="RunSummaryDto"/>, one SQL query, no tracking - filter, order and page <paramref name="runs"/> first - EF cannot translate a filter or ordering over the projected DTO.</summary>
    private static IQueryable<RunSummaryDto> Project(IQueryable<Run> runs) =>
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

    /// <summary>Fills <see cref="RunSummaryDto.StagesExpected"/> for the runs still in flight.</summary>
    private static async Task<List<RunSummaryDto>> WithExpectedStagesAsync(
        ServerDbContext db, List<RunSummaryDto> runs, CancellationToken cancellationToken)
    {
        var expected = new Dictionary<Guid, int>();
        foreach (var pipelineId in runs.Where(r => ActiveStatuses.Contains(r.Status)).Select(r => r.PipelineId).Distinct())
        {
            var names = await ExpectedStageNamesAsync(db, pipelineId, excludeRunId: null, cancellationToken).ConfigureAwait(false);
            if (names.Count > 0)
            {
                expected[pipelineId] = names.Count;
            }
        }

        return runs
            .Select(r => ActiveStatuses.Contains(r.Status) && expected.TryGetValue(r.PipelineId, out var count)
                ? r with { StagesExpected = count }
                : r)
            .ToList();
    }

    /// <summary>The stage names of <paramref name="pipelineId"/>'s most recent successful run, in order.</summary>
    private static async Task<List<string>> ExpectedStageNamesAsync(
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

    internal static async Task<RunDetailDto?> LoadRunDetailAsync(ServerDbContext db, Guid runId, CancellationToken cancellationToken)
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
            .Take(120)
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
