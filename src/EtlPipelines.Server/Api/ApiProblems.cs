using Grpc.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EtlPipelines.Server.Api;

/// <summary>Turns the failures of operations shared with the gRPC services into the REST API's error responses.</summary>
internal static class ApiProblems
{
    /// <summary>The problem-details response for <paramref name="exception"/>, with the same reason the gRPC caller would get.</summary>
    public static ProblemHttpResult From(RpcException exception)
    {
        var status = exception.StatusCode switch
        {
            StatusCode.NotFound => StatusCodes.Status404NotFound,
            StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
            StatusCode.FailedPrecondition => StatusCodes.Status409Conflict,
            StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
            StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
            StatusCode.Internal => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        };

        return TypedResults.Problem(detail: exception.Status.Detail, statusCode: status);
    }
}
