using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Abstractions.Lifecycle;
using EtlPipelines.Abstractions.Ports;

namespace EtlPipelines.Core.Tests.Fixtures;

/// <summary>A source that hands out a fixed sequence of rows, honouring the buffer-fill contract.</summary>
public sealed class InMemorySource<TRow>(IEnumerable<TRow> rows) : IDataSource<TRow>
{
    private readonly TRow[] _rows = rows.ToArray();
    private int _position;

    /// <summary>Caps how many rows a single read returns, to exercise short reads.</summary>
    public int? MaxRowsPerRead { get; init; }

    /// <summary>Delay applied to each read, for timing and back-pressure tests.</summary>
    public TimeSpan ReadDelay { get; init; }

    public int Disposals { get; private set; }

    /// <summary>Rows handed downstream so far, for measuring how far the source has run ahead.</summary>
    public int Produced => _position;

    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (ReadDelay > TimeSpan.Zero)
        {
            await Task.Delay(ReadDelay, cancellationToken);
        }

        var remaining = _rows.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var count = Math.Min(Math.Min(remaining, buffer.Length), MaxRowsPerRead ?? int.MaxValue);
        _rows.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;

        return count;
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A sink that records everything written to it.</summary>
public sealed class InMemorySink<TRow> : IDataSink<TRow>, IAsyncCompletable
{
    private readonly List<TRow> _rows = [];
    private readonly Lock _gate = new();

    /// <summary>Delay applied to each write, for back-pressure tests.</summary>
    public TimeSpan WriteDelay { get; init; }

    /// <summary>When set, the sink rejects any batch containing a row matching this predicate.</summary>
    public Func<TRow, bool>? Reject { get; init; }

    public int Completions { get; private set; }

    public int Disposals { get; private set; }

    public IReadOnlyList<TRow> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows.ToArray();
            }
        }
    }

    public async ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        if (WriteDelay > TimeSpan.Zero)
        {
            await Task.Delay(WriteDelay, cancellationToken);
        }

        // Copy out before returning: the runtime recycles this buffer as soon as the call completes,
        // so retaining the memory would be a use-after-free in all but name.
        var copied = batch.ToArray();

        if (Reject is not null && Array.Exists(copied, row => Reject(row)))
        {
            return Error.Validation("sink.rejected", "Batch contained a rejected row.");
        }

        lock (_gate)
        {
            _rows.AddRange(copied);
        }

        return copied.Length;
    }

    public ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        Completions++;
        return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Collects dead-lettered rows so tests can assert on what was diverted.</summary>
public sealed class RecordingDeadLetterSink<TRow> : IDeadLetterSink<TRow>
{
    private readonly List<(TRow Row, Error Error)> _entries = [];

    public IReadOnlyList<(TRow Row, Error Error)> Entries => _entries;

    public ValueTask WriteAsync(TRow row, Error error, CancellationToken cancellationToken)
    {
        _entries.Add((row, error));
        return ValueTask.CompletedTask;
    }
}
