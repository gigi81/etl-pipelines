using EtlPipelines.Server.Live.Events;

namespace EtlPipelines.Server.Live;

/// <summary>
/// The events that happened but are not in the database yet, in the order they happened. The sync
/// service takes them in batches; a batch that could not be written goes back to the front.
/// </summary>
internal sealed class EventOutbox
{
    private readonly object _gate = new();
    private List<LiveEvent> _events = [];

    /// <summary>How many events are waiting.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    public void Add(LiveEvent liveEvent)
    {
        lock (_gate)
        {
            _events.Add(liveEvent);
        }
    }

    /// <summary>Takes everything waiting, oldest first.</summary>
    public IReadOnlyList<LiveEvent> Drain()
    {
        lock (_gate)
        {
            var batch = _events;
            _events = [];
            return batch;
        }
    }

    /// <summary>Puts a batch that was not written back in front of whatever arrived since it was taken, so order is kept.</summary>
    public void Requeue(IReadOnlyList<LiveEvent> batch)
    {
        lock (_gate)
        {
            _events.InsertRange(0, batch);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _events = [];
        }
    }
}
