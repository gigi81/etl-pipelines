using EtlPipelines.PipelineExecution.V1;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Live;
using EtlPipelines.Server.Runs;
using EtlPipelines.Server.Secrets;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="PipelineExecutionService"/> - see that proto's own comments
/// (<c>protos/v1/pipeline_execution.proto</c>) for who calls this and why every RPC is scoped to
/// one <c>session_id</c>.
/// </summary>
/// <remarks>
/// <c>session_id</c> is the run's id itself, minted by <see cref="RunDispatcher"/> before the work
/// item that carries it is ever sent. Every RPC here resolves it against <see cref="LiveState"/> -
/// where every run in flight is held - and fails with <see cref="StatusCode.NotFound"/> for one the
/// server does not know. What a process reports goes into <see cref="LiveState"/> and is written to
/// the database a few seconds later by <see cref="LiveStateSync"/>; the database is not touched here
/// (the configuration entries a run pulls are the one thing read from it).
/// </remarks>
public sealed class PipelineExecutionServiceImpl(SecretsStore secretsStore, LiveState liveState)
    : PipelineExecutionService.PipelineExecutionServiceBase
{
    /// <inheritdoc />
    public override async Task<GetConfigurationResponse> GetConfiguration(GetConfigurationRequest request, ServerCallContext context)
    {
        var runId = await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);

        // The first call this service sees for a run - pulling configuration is the very first
        // thing a launched pipeline process does (EtlPipelines.GrpcClient.GrpcConfigurationSource
        // runs during host configuration, before anything else). Marking the run as started here,
        // rather than waiting for the agent's own ReportExecutionStatus(STARTED), is what flips it
        // to running even if that agent-side signal is slow or lost.
        await liveState.StartRunAsync(runId, context.CancellationToken).ConfigureAwait(false);

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
        var runId = await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);

        var errorCode = string.IsNullOrEmpty(request.ErrorCode) ? null : request.ErrorCode;
        var errorDescription = string.IsNullOrEmpty(request.ErrorDescription) ? null : request.ErrorDescription;

        await liveState.StageCompletedAsync(
            runId,
            new StageDto(
                request.Sequence, request.Name, request.RowsIn, request.RowsOut, request.RowsFailed, request.ElapsedMs, errorCode, errorDescription),
            context.CancellationToken).ConfigureAwait(false);

        return new PipelineExecution.V1.Ack();
    }

    /// <inheritdoc />
    public override async Task<PipelineExecution.V1.Ack> ReportRunResult(ReportRunResultRequest request, ServerCallContext context)
    {
        var runId = await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);
        var status = request.Outcome == ReportRunResultRequest.Types.Outcome.Succeeded ? "Succeeded" : "Failed";

        await liveState.FinishRunAsync(
            runId, status, request.ExitCode, request.RowsRead, request.RowsWritten, request.RowsFailed, context.CancellationToken).ConfigureAwait(false);

        return new PipelineExecution.V1.Ack();
    }

    /// <inheritdoc />
    public override async Task<PipelineExecution.V1.Ack> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        // Still nothing to update - SERVER.md Phase 8's own "Agent liveness" is scoped to exactly
        // that, the agent's own heartbeat, not a per-run one here. A launched process that hangs
        // (never exits, never calls ReportRunResult) while its own agent stays perfectly healthy
        // is therefore still not caught by anything - a real liveness use for this RPC is
        // deliberately left as a known, separate gap. This call still exists to validate the
        // session and give a launched process something well-defined to call either way.
        await FindRunAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);
        return new PipelineExecution.V1.Ack();
    }

    /// <summary>The id of the run <paramref name="sessionId"/> names, which must be one the server holds.</summary>
    private async Task<Guid> FindRunAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var runId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{sessionId}' is not a valid session id."));
        }

        // Held in memory for as long as it is in flight: no database read, and no waiting for one to
        // have caught up with a run that was created a moment ago.
        return await liveState.HoldsRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ? runId
            : throw new RpcException(new Status(StatusCode.NotFound, $"No run '{sessionId}' is known to this server."));
    }
}
