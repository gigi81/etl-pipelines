using System.Buffers;

namespace EtlPipelines.Runtime;

/// <summary>
/// A batch of rows in a pooled array, moving between pipeline stages through a channel.
/// </summary>
/// <remarks>
/// The array is rented from <see cref="ArrayPool{T}"/> and returned once the receiving stage has
/// finished with it, so steady-state batch churn allocates nothing. Ownership passes with the batch:
/// whoever takes it off the channel is responsible for returning it, and must not hand the memory to
/// anything that will retain it.
/// </remarks>
internal readonly struct PooledBatch<T>(T[] buffer, int count)
{
    private readonly T[] _buffer = buffer;

    /// <summary>Rows in this batch.</summary>
    public int Count { get; } = count;

    /// <summary>The rows, as a view over the pooled array.</summary>
    public ReadOnlyMemory<T> Memory => _buffer.AsMemory(0, Count);

    /// <summary>Rents a buffer sized for at least <paramref name="capacity"/> rows.</summary>
    public static T[] Rent(int capacity) => ArrayPool<T>.Shared.Rent(capacity);

    /// <summary>Returns a rented buffer to the pool.</summary>
    public static void Return(T[] buffer) =>
        // Reference rows must be cleared or the pool would keep them alive until the buffer is
        // handed out again, which for large batches of large objects is a real leak.
        ArrayPool<T>.Shared.Return(buffer, clearArray: System.Runtime.CompilerServices
            .RuntimeHelpers.IsReferenceOrContainsReferences<T>());

    /// <summary>Returns this batch's buffer to the pool.</summary>
    public void Release() => Return(_buffer);
}
