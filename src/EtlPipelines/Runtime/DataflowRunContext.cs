
namespace EtlPipelines.Runtime;

/// <summary>
/// Shared state for the nodes of one dataflow stage while it runs.
/// </summary>
/// <remarks>
/// Nodes run concurrently, so when one fails the others must stop rather than block forever on a
/// channel that will never drain. <see cref="Fail"/> records the first error and trips the shared
/// token; every node treats cancellation as "wind up quietly" and the stage reports the recorded
/// error rather than the cancellation that followed from it.
/// </remarks>
internal sealed class DataflowRunContext : IDisposable
{
    private readonly CancellationTokenSource _abort;
    private readonly Lock _gate = new();
    private Error? _firstError;

    public DataflowRunContext(PipelineContext pipeline, RowErrorTracker errors, CancellationToken external)
    {
        Pipeline = pipeline;
        Errors = errors;
        ExternalToken = external;
        _abort = CancellationTokenSource.CreateLinkedTokenSource(external);
    }

    public PipelineContext Pipeline { get; }

    public PipelineOptions Options => Pipeline.Options;

    public RowErrorTracker Errors { get; }

    /// <summary>The caller's token, used to tell user cancellation apart from an internal abort.</summary>
    public CancellationToken ExternalToken { get; }

    /// <summary>Token every node observes. Trips on external cancellation or any node's failure.</summary>
    public CancellationToken Token => _abort.Token;

    /// <summary>The error that stopped the stage, if any.</summary>
    public Error? FirstError
    {
        get
        {
            lock (_gate)
            {
                return _firstError;
            }
        }
    }

    /// <summary>Records a fatal error and stops the other nodes.</summary>
    public void Fail(Error error)
    {
        lock (_gate)
        {
            _firstError ??= error;
        }

        if (!_abort.IsCancellationRequested)
        {
            _abort.Cancel();
        }
    }

    public void Dispose() => _abort.Dispose();
}
