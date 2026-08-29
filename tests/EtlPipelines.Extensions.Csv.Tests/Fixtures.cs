using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Abstractions.Ports;

namespace EtlPipelines.Csv.Tests;

/// <summary>Feeds a fixed array of rows into a pipeline.</summary>
public sealed class ArraySource<TRow>(IReadOnlyList<TRow> rows) : IDataSource<TRow>
{
    private int _position;

    public ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        var count = Math.Min(buffer.Length, rows.Count - _position);
        if (count <= 0)
        {
            return ValueTask.FromResult<ErrorOr<int>>(0);
        }

        for (var i = 0; i < count; i++)
        {
            buffer.Span[i] = rows[_position + i];
        }

        _position += count;
        return ValueTask.FromResult<ErrorOr<int>>(count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Collects whatever a pipeline writes to it.</summary>
public sealed class CollectingSink<TRow> : IDataSink<TRow>
{
    private readonly List<TRow> _rows = [];

    /// <summary>Fails the batch containing any row matching this predicate.</summary>
    public Func<TRow, bool>? Reject { get; init; }

    public IReadOnlyList<TRow> Rows => _rows;

    public ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<TRow> batch, CancellationToken cancellationToken)
    {
        var copied = batch.ToArray();

        if (Reject is not null && Array.Exists(copied, row => Reject(row)))
        {
            return ValueTask.FromResult<ErrorOr<int>>(
                Error.Validation("sink.rejected", "Batch contained a rejected row."));
        }

        _rows.AddRange(copied);
        return ValueTask.FromResult<ErrorOr<int>>(copied.Length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Fails once it has seen a given number of rows, to exercise mid-run failure.</summary>
/// <remarks>
/// <paramref name="delayPerBatch"/> matters when this sits alongside another branch: without it this
/// sink consumes its batches and fails so quickly that the sibling branch may never write anything,
/// which makes "the run failed part-way through" untrue in the only sense a test cares about.
/// </remarks>
public sealed class FailingSink<TRow>(int failAfter, TimeSpan delayPerBatch = default) : IDataSink<TRow>
{
    private int _seen;

    public async ValueTask<ErrorOr<int>> WriteAsync(
        ReadOnlyMemory<TRow> batch,
        CancellationToken cancellationToken)
    {
        if (delayPerBatch > TimeSpan.Zero)
        {
            await Task.Delay(delayPerBatch, cancellationToken);
        }

        _seen += batch.Length;

        return _seen >= failAfter
            ? Error.Failure("sink.exploded", "Deliberate failure.")
            : batch.Length;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records dead-lettered rows so tests can assert on what was diverted.</summary>
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
