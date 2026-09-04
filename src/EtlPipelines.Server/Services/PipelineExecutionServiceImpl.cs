using EtlPipelines.Management.V1;
using EtlPipelines.PipelineExecution.V1;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Secrets;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="PipelineExecutionService"/> - see that proto's own comments
/// (<c>protos/v1/pipeline_execution.proto</c>) for who calls this and why every RPC is scoped to
/// one <c>session_id</c>.
/// </summary>
/// <remarks>
/// SERVER.md Phase 6: <c>session_id</c> is <c>Runs.Id</c> itself, minted by
/// <see cref="RunDispatcher"/> before the work item that carries it is ever sent - every RPC here
/// resolves it against <see cref="ServerDbContext.Runs"/> and fails with
/// <see cref="StatusCode.NotFound"/> for one this server has no row for.
/// </remarks>
public sealed class PipelineExecutionServiceImpl(ServerDbContext dbContext, SecretsStore secretsStore, RunStatusStore statusStore)
    : PipelineExecutionService.PipelineExecutionServiceBase
{
    /// <inheritdoc />
    public override async Task<GetConfigurationResponse> GetConfiguration(GetConfigurationRequest request, ServerCallContext context)
    {
        var run = await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);

        // The first call this service sees for a run - pulling configuration is the very first
        // thing a launched pipeline process does (EtlPipelines.GrpcClient.GrpcConfigurationSource
        // runs during host configuration, before anything else). Flipping Dispatched -> Running
        // here, rather than waiting for the agent's own ReportExecutionStatus(STARTED), is what
        // marks a run as actually under way even if that agent-side signal is slow or lost.
        if (run.Status == "Dispatched")
        {
            run.Status = "Running";
            run.StartedAt ??= DateTime.UtcNow;
            await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
        }

        var entries = await secretsStore.GetAllAsync(context.CancellationToken).ConfigureAwait(false);

        var response = new GetConfigurationResponse();
        foreach (var (key, value) in entries)
        {
            response.Entries[key] = value;
        }

        return response;
    }

    /// <inheritdoc />
    public override async Task<PipelineExecution.V1.Ack> ReportStageResult(ReportStageResultRequest request, ServerCallContext context)
    {
        var run = await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);

        var errorCode = string.IsNullOrEmpty(request.ErrorCode) ? null : request.ErrorCode;
        var errorDescription = string.IsNullOrEmpty(request.ErrorDescription) ? null : request.ErrorDescription;

        dbContext.StageResults.Add(new StageResult
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Sequence = request.Sequence,
            Name = request.Name,
            RowsIn = request.RowsIn,
            RowsOut = request.RowsOut,
            RowsFailed = request.RowsFailed,
            ElapsedMs = request.ElapsedMs,
            ErrorCode = errorCode,
            ErrorDescription = errorDescription,
        });
        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        statusStore.PublishStageCompleted(run.Id, new StageCompleted
        {
            Sequence = request.Sequence,
            Name = request.Name,
            RowsIn = request.RowsIn,
            RowsOut = request.RowsOut,
            RowsFailed = request.RowsFailed,
            ElapsedMs = request.ElapsedMs,
            ErrorCode = errorCode ?? string.Empty,
            ErrorDescription = errorDescription ?? string.Empty,
        });

        return new PipelineExecution.V1.Ack();
    }

    /// <inheritdoc />
    public override async Task<PipelineExecution.V1.Ack> ReportRunResult(ReportRunResultRequest request, ServerCallContext context)
    {
        var run = await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);
        var succeeded = request.Outcome == ReportRunResultRequest.Types.Outcome.Succeeded;

        run.Status = succeeded ? "Succeeded" : "Failed";
        run.CompletedAt = DateTime.UtcNow;
        run.ExitCode = request.ExitCode;
        run.RowsRead = request.RowsRead;
        run.RowsWritten = request.RowsWritten;
        run.RowsFailed = request.RowsFailed;
        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);

        statusStore.PublishRunCompleted(run.Id, new RunCompleted
        {
            Status = succeeded ? RunCompleted.Types.Status.Succeeded : RunCompleted.Types.Status.Failed,
            ExitCode = request.ExitCode,
            RowsRead = request.RowsRead,
            RowsWritten = request.RowsWritten,
            RowsFailed = request.RowsFailed,
        });

        return new PipelineExecution.V1.Ack();
    }

    /// <inheritdoc />
    public override async Task<PipelineExecution.V1.Ack> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        // Still nothing to update - SERVER.md Phase 8's own "Agent liveness" is scoped to exactly
        // that, the agent's own heartbeat (Agents.LastHeartbeatAt, watched by
        // Agents.AgentLivenessMonitor), not a per-run one here. A launched process that hangs
        // (never exits, never calls ReportRunResult) while its own agent stays perfectly healthy
        // is therefore still not caught by anything - a real liveness use for this RPC, and the
        // schema column it would need, is deliberately left as a known, separate gap rather than
        // folded into this phase. This call still exists to validate the session and give a
        // launched process something well-defined to call either way.
        await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);
        return new PipelineExecution.V1.Ack();
    }

    private async Task<Run> FindRunAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var runId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{sessionId}' is not a valid session id."));
        }

        var run = await dbContext.Runs
            .SingleOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false);

        return run ?? throw new RpcException(new Status(StatusCode.NotFound, $"No run '{sessionId}' is known to this server."));
    }
}
