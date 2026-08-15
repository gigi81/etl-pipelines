namespace EtlPipelines.Abstractions.Lifecycle;

/// <summary>
/// Opt-in capability: the runtime calls <see cref="CompleteAsync"/> exactly once after the last row
/// has been handled successfully, and never on a failure path.
/// </summary>
/// <remarks>
/// This is the flush-and-commit signal, and it is distinct from
/// <see cref="IAsyncDisposable.DisposeAsync"/> in two ways that matter. Disposal runs on failure and
/// cancellation paths too, where committing a half-written load is precisely wrong; and disposal
/// cannot report an error, so a bulk-copy that fails on final flush would be swallowed. Completion
/// runs only on success and returns an <see cref="ErrorOr{TValue}"/>.
/// </remarks>
public interface IAsyncCompletable
{
    /// <summary>
    /// Flushes buffered work and commits. Called once, only when the stream completed without error.
    /// </summary>
    ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken);
}
