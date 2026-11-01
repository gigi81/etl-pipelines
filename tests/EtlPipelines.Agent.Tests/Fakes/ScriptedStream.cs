using EtlPipelines.AgentExecution.V1;
using Grpc.Core;

namespace EtlPipelines.Agent.Tests.Fakes;

/// <summary>The work items stream a fake server sends: some items, then it fails, ends or just stays open.</summary>
internal sealed class ScriptedStream(IEnumerable<WorkItem> items, ScriptedStream.Ending ending) : IAsyncStreamReader<WorkItem>
{
    /// <summary>What happens after the items.</summary>
    public enum Ending
    {
        /// <summary>The call fails, as when the server goes away.</summary>
        Fails,

        /// <summary>The server ends the stream.</summary>
        Completes,

        /// <summary>The stream stays open until the caller gives up on it.</summary>
        StaysOpen,
    }

    private readonly Queue<WorkItem> _items = new(items);

    /// <inheritdoc />
    public WorkItem Current { get; private set; } = new();

    /// <inheritdoc />
    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_items.TryDequeue(out var next))
        {
            Current = next;
            return true;
        }

        switch (ending)
        {
            case Ending.Fails:
                throw new RpcException(new Status(StatusCode.Unavailable, "the server went away"));
            case Ending.Completes:
                return false;
            default:
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return false;
        }
    }
}
