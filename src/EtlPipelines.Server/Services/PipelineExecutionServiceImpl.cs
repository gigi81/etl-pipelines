using EtlPipelines.PipelineExecution.V1;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="PipelineExecutionService"/> - see that proto's own comments
/// (<c>protos/v1/pipeline_execution.proto</c>) for who calls this and why every RPC is scoped to
/// one <c>session_id</c>.
/// </summary>
/// <remarks>
/// Phase 2 scaffolding: every method exists so the full v1 surface is hosted and the solution
/// builds clean, but none has real behaviour yet - <see cref="ServiceScaffolding.Unimplemented"/>
/// is what every one of them returns until the phase that actually needs it lands
/// (<see cref="GetConfiguration"/> in Phase 6, alongside the report/heartbeat RPCs).
/// </remarks>
public sealed class PipelineExecutionServiceImpl : PipelineExecutionService.PipelineExecutionServiceBase
{
    /// <inheritdoc />
    public override Task<GetConfigurationResponse> GetConfiguration(
        GetConfigurationRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> ReportStageResult(ReportStageResultRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> ReportRunResult(ReportRunResultRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> Heartbeat(HeartbeatRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();
}
