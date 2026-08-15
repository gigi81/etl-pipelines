using System.Threading.Channels;

namespace EtlPipelines.Runtime;

/// <summary>
/// One link in a dataflow: a source, a transform or a sink.
/// </summary>
/// <remarks>
/// A dataflow chains rows through several row types, so the nodes cannot share a single generic
/// signature. This non-generic base is the erasure boundary: each node knows its own input and output
/// types internally and exchanges channels as <see cref="object"/>, which the next node casts back to
/// the type its own generic parameters prove correct. The builder is what guarantees those casts are
/// sound, by refusing to chain steps whose types do not line up.
/// </remarks>
internal abstract class DataflowNode
{
    /// <summary>The node's name, for diagnostics.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// Produces a fresh node with the same configuration.
    /// </summary>
    /// <remarks>
    /// The builder assembles one set of nodes as a template, but a node accumulates per-run state —
    /// row counters, its channel, its completion task. Each run therefore gets its own set, so counts
    /// do not carry over between runs and two concurrent runs of the same pipeline cannot race on the
    /// same fields.
    /// </remarks>
    public abstract DataflowNode CreateInstance();

    /// <summary>Rows this node consumed.</summary>
    public long RowsIn { get; protected set; }

    /// <summary>Rows this node produced.</summary>
    public long RowsOut { get; protected set; }

    /// <summary>
    /// Starts pumping and returns the reader downstream should consume, or <see langword="null"/> for
    /// a terminal node.
    /// </summary>
    /// <param name="input">The upstream reader, or <see langword="null"/> for a source.</param>
    /// <param name="context">Shared state for the stage: options, error tracking and the abort token.</param>
    public abstract object? Start(object? input, DataflowRunContext context);

    /// <summary>Completes when this node has finished pumping.</summary>
    public Task Completion { get; protected set; } = Task.CompletedTask;

        /// <summary>Creates the bounded channel that carries batches to the next node.</summary>
    protected static Channel<PooledBatch<T>> CreateChannel<T>(DataflowRunContext context, bool singleWriter) =>
        Channel.CreateBounded<PooledBatch<T>>(
            new BoundedChannelOptions(context.Options.ChannelCapacity)
            {
                // Waiting on a full channel is the whole point: it is what makes a slow sink throttle
                // the source instead of letting batches pile up without limit.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = singleWriter,
            });

    /// <summary>Runs a port's opt-in initialization, if it declares any.</summary>
    protected static async ValueTask InitializeAsync(object port, CancellationToken cancellationToken)
    {
        if (port is IAsyncInitializable initializable)
        {
            await initializable.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs a port's opt-in completion. Only ever called on the success path.</summary>
    protected static async ValueTask<ErrorOr<Success>> CompleteAsync(object port, CancellationToken cancellationToken)
    {
        if (port is IAsyncCompletable completable)
        {
            return await completable.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success;
    }

    /// <summary>Disposes a port, tolerating either disposal interface or neither.</summary>
    protected static async ValueTask DisposeAsync(object? port)
    {
        switch (port)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>Converts an unexpected exception into the error that will fail the run.</summary>
    protected static Error ToError(string node, Exception exception) =>
        Error.Failure($"stage.{node}.faulted", $"{exception.GetType().Name}: {exception.Message}");
}

/// <summary>A node whose degree of parallelism can be set after it has been appended to the chain.</summary>
internal interface IParallelizable
{
    int DegreeOfParallelism { get; set; }
}
