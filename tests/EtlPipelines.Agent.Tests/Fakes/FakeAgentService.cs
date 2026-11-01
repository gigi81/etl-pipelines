using EtlPipelines.AgentExecution.V1;
using Grpc.Core;
using Moq;

namespace EtlPipelines.Agent.Tests.Fakes;

/// <summary>
/// A server for the agent to talk to, scripted by the test: it issues ids the way the real one does (a new one,
/// or the one the agent brings), and each call is answered by a function the test can change.
/// </summary>
internal sealed class FakeAgentService
{
    private readonly object _gate = new();
    private int _subscribes;

    public FakeAgentService()
    {
        var mock = new Mock<AgentService.AgentServiceClient> { CallBase = true };

        mock.Setup(c => c.RegisterAgentAsync(It.IsAny<RegisterAgentRequest>(), It.IsAny<CallOptions>()))
            .Returns((RegisterAgentRequest request, CallOptions _) =>
            {
                int attempt;
                lock (_gate)
                {
                    Registrations.Add(request);
                    attempt = Registrations.Count;
                }

                if (RefuseRegistration(attempt))
                {
                    return Failure<RegisterAgentResponse>(StatusCode.Unavailable, "not up yet");
                }

                var issued = request.AgentId.Length > 0 ? request.AgentId : Guid.NewGuid().ToString();
                lock (_gate)
                {
                    Issued.Add(issued);
                }

                return Unary(new RegisterAgentResponse { AgentId = issued });
            });

        mock.Setup(c => c.HeartbeatAsync(It.IsAny<AgentHeartbeatRequest>(), It.IsAny<CallOptions>()))
            .Returns((AgentHeartbeatRequest request, CallOptions _) => OnHeartbeat(request));

        mock.Setup(c => c.Subscribe(It.IsAny<SubscribeRequest>(), It.IsAny<CallOptions>()))
            .Returns((SubscribeRequest request, CallOptions _) =>
            {
                int number;
                lock (_gate)
                {
                    number = ++_subscribes;
                }

                return new AsyncServerStreamingCall<WorkItem>(
                    OnSubscribe(number, request), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
            });

        mock.Setup(c => c.ReportExecutionStatusAsync(It.IsAny<ReportExecutionStatusRequest>(), It.IsAny<CallOptions>()))
            .Returns((ReportExecutionStatusRequest request, CallOptions _) => OnReport(request));

        Client = mock.Object;
    }

    public AgentService.AgentServiceClient Client { get; }

    /// <summary>Every registration received, in order - the refused ones too.</summary>
    public List<RegisterAgentRequest> Registrations { get; } = [];

    /// <summary>The ids issued, in order (a refused registration issues none).</summary>
    public List<string> Issued { get; } = [];

    /// <summary>Whether to refuse the n-th registration (1 is the first), as a server that is not up yet does. Accepts all by default.</summary>
    public Func<int, bool> RefuseRegistration { get; set; } = _ => false;

    /// <summary>How many times the agent subscribed.</summary>
    public int SubscribeCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribes;
            }
        }
    }

    /// <summary>How the server answers a heartbeat. Answers every one by default.</summary>
    public Func<AgentHeartbeatRequest, AsyncUnaryCall<Ack>> OnHeartbeat { get; set; } = _ => Unary(new Ack());

    /// <summary>The stream sent for the n-th subscribe (1 is the first). Stays open by default.</summary>
    public Func<int, SubscribeRequest, IAsyncStreamReader<WorkItem>> OnSubscribe { get; set; } =
        (_, _) => new ScriptedStream([], ScriptedStream.Ending.StaysOpen);

    /// <summary>How the server answers a report on a run. Takes every one by default.</summary>
    public Func<ReportExecutionStatusRequest, AsyncUnaryCall<Ack>> OnReport { get; set; } = _ => Unary(new Ack());

    public int RegistrationCount
    {
        get
        {
            lock (_gate)
            {
                return Registrations.Count;
            }
        }
    }

    public static AsyncUnaryCall<T> Unary<T>(T response) =>
        new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });

    public static AsyncUnaryCall<T> Failure<T>(StatusCode code, string detail = "failed") =>
        new(
            Task.FromException<T>(new RpcException(new Status(code, detail))),
            Task.FromResult(new Metadata()),
            () => new Status(code, detail),
            () => [],
            () => { });
}
