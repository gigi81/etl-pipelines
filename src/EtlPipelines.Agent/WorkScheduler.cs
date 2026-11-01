using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Agent;

/// <summary>
/// Runs the agent's work items in the background, at the same time: a long pipeline run never holds up
/// an install, or another run, unless every run slot is taken.
/// </summary>
/// <remarks>
/// Two lanes. <see cref="RunLimited"/> is for pipeline runs: at most <c>maxConcurrent</c> go at once and the
/// rest wait their turn, so a burst of runs cannot start more processes than the machine should carry.
/// <see cref="RunUnlimited"/> is for everything else (installs), which starts at once.
/// Whatever is running is tracked so the agent can wait for it to finish when it shuts down
/// (<see cref="WhenIdleAsync"/>). Work is expected to handle and report its own failures; one that does not is
/// logged and never takes the agent down.
/// </remarks>
public sealed class WorkScheduler(int maxConcurrent, ILogger logger)
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, maxConcurrent));
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private int _running;
    private int _waiting;

    /// <summary>How many limited work items are running now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>How many limited work items are waiting for a slot.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>Starts <paramref name="work"/> once a run slot is free. Returns at once.</summary>
    public Task RunLimited(Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        Track(Task.Run(() => LimitedAsync(work, cancellationToken), CancellationToken.None));

    /// <summary>Starts <paramref name="work"/> now, whatever else is running. Returns at once.</summary>
    public Task RunUnlimited(Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        Track(Task.Run(() => GuardedAsync(work, cancellationToken), CancellationToken.None));

    /// <summary>Completes when everything started so far has finished (or been cancelled).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Keys);

    private async Task LimitedAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        Interlocked.Increment(ref _running);
        try
        {
            await GuardedAsync(work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _slots.Release();
        }
    }

    private async Task GuardedAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            // A slot freed by the cancellation of what held it can be won by work that was waiting for it, just
            // before that work's own cancellation is noticed: it must not start then.
            cancellationToken.ThrowIfCancellationRequested();
            await work(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The agent is stopping.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A work item failed in a way it did not report.");
        }
    }

    private Task Track(Task task)
    {
        _inFlight[task] = 0;
        _ = task.ContinueWith(finished => _inFlight.TryRemove(finished, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
}
