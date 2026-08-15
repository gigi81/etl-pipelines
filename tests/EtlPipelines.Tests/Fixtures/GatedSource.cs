using EtlPipelines.Abstractions.Ports;

namespace EtlPipelines.Tests.Fixtures;

/// <summary>
/// A source that stops after a set number of rows until released, so a test can observe what has
/// reached the sink while the input is provably still open.
/// </summary>
/// <remarks>
/// This is what makes "semi-blocking" testable rather than a claim: a sorted aggregate must emit
/// completed groups while the gate is still shut, and a hash aggregate must not.
/// </remarks>
public sealed class GatedSource<TRow>(IReadOnlyList<TRow> rows, int gateAfter) : IDataSource<TRow>
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    /// <summary>Rows handed out so far.</summary>
    public int Produced => _position;

    /// <summary>Lets the source continue past the gate.</summary>
    public void Release() => _release.TrySetResult();

    public async ValueTask<ErrorOr<int>> ReadAsync(Memory<TRow> buffer, CancellationToken cancellationToken)
    {
        if (_position >= gateAfter)
        {
            await _release.Task.WaitAsync(cancellationToken);
        }

        var remaining = rows.Count - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        // Never cross the gate in a single read, so the pause lands exactly where the test expects.
        var limit = _position < gateAfter ? gateAfter - _position : remaining;
        var count = Math.Min(Math.Min(remaining, buffer.Length), limit);

        for (var i = 0; i < count; i++)
        {
            buffer.Span[i] = rows[_position + i];
        }

        _position += count;
        return count;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
