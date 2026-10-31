using System.Threading.Channels;

namespace EtlPipelines.Server.Live;

/// <summary>
/// One listener to <see cref="LiveState"/>'s changes. It remembers <em>which</em> things changed since
/// it last looked, not every change: a burst of updates to the same run is one pending entry, so a
/// slow listener never builds up a backlog, and no change is ever lost. Dispose it to stop listening.
/// </summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<LiveChange, bool> _filter;
    private readonly Action<LiveSubscription> _onDispose;

    // At most one "something is pending" token - the pending set itself is what holds the changes.
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private HashSet<LiveChange> _pending = [];
    private bool _disposed;

    internal LiveSubscription(Func<LiveChange, bool> filter, Action<LiveSubscription> onDispose)
    {
        _filter = filter;
        _onDispose = onDispose;
    }

    /// <summary>Records <paramref name="change"/> as pending if this subscription is interested in it.</summary>
    internal void Offer(LiveChange change)
    {
        if (!_filter(change))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(change);
        }

        _signal.Writer.TryWrite(true);
    }

    /// <summary>
    /// Waits until something changed and returns what, or returns an empty list once
    /// <paramref name="timeout"/> passes with nothing new - or <paramref name="cancellationToken"/>
    /// fires - so a caller can send a keep-alive and look at its own cancellation.
    /// </summary>
    public async ValueTask<IReadOnlyList<LiveChange>> NextAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await _signal.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The timeout, or the caller's own cancellation - either way, whatever is pending (if
            // anything slipped in between) is returned below, and an empty list means "nothing".
        }
        catch (ChannelClosedException)
        {
            return [];
        }

        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            var changes = _pending.ToList();
            _pending = [];
            return changes;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
        }

        _signal.Writer.TryComplete();
        _onDispose(this);
    }
}
