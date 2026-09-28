using System.Runtime.CompilerServices;
using Grpc.Core;

namespace EtlPipelines.Server.Services;

/// <summary>Shared by every Phase 2 stub RPC across all three v1 services.</summary>
internal static class ServiceScaffolding
{
    /// <summary>
    /// The response a stub RPC gives instead of throwing something a caller would have to guess
    /// the meaning of - <see cref="StatusCode.Unimplemented"/> is what gRPC itself defines for
    /// exactly this ("the operation is not implemented or is not supported/enabled"), so a client
    /// hitting a not-yet-built method gets a well-understood, standard error rather than an
    /// opaque 500-equivalent.
    /// </summary>
    public static RpcException Unimplemented([CallerMemberName] string method = "") =>
        new(new Status(
            StatusCode.Unimplemented,
            $"{method} is part of the v1 contract but not implemented yet."));

    /// <summary>
    /// What <see cref="ManagementServiceImpl.InstallPackage"/>/<see cref="ManagementServiceImpl.ExecutePipeline"/>
    /// give when no agent is connected to delegate to -
    /// <see cref="StatusCode.FailedPrecondition"/> ("the system is not in a state required for the
    /// operation's execution") rather than <see cref="Unimplemented"/>, since both RPCs are fully
    /// implemented; there is simply nothing connected right now that could carry them out.
    /// </summary>
    public static RpcException NoAgentsAvailable([CallerMemberName] string method = "") =>
        new(new Status(
            StatusCode.FailedPrecondition,
            $"{method} needs an agent to delegate to, and none is connected."));
}
