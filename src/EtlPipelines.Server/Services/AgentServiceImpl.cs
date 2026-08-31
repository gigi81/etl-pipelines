using EtlPipelines.AgentExecution.V1;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>
/// Serves <see cref="AgentService"/> - see that proto's own comments
/// (<c>protos/v1/agent_execution.proto</c>) for who calls this and why it is a separate, broader
/// surface than <see cref="PipelineExecutionServiceImpl"/>.
/// </summary>
/// <remarks>
/// Phase 2 scaffolding: every method exists so the full v1 surface is hosted and the solution
/// builds clean, but none has real behaviour yet - <see cref="ServiceScaffolding.Unimplemented"/>
/// is what every one of them returns until Phase 5 (registration, install delegation) and Phase 6
/// (execution dispatch) fill them in.
/// </remarks>
public sealed class AgentServiceImpl : AgentService.AgentServiceBase
{
    /// <inheritdoc />
    public override Task<RegisterAgentResponse> RegisterAgent(RegisterAgentRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> Heartbeat(AgentHeartbeatRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task Subscribe(
        SubscribeRequest request, IServerStreamWriter<WorkItem> responseStream, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> ReportInstallResult(ReportInstallResultRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();

    /// <inheritdoc />
    public override Task<Ack> ReportExecutionStatus(ReportExecutionStatusRequest request, ServerCallContext context) =>
        throw ServiceScaffolding.Unimplemented();
}
