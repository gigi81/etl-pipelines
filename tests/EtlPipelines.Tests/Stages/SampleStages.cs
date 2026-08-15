using System.ComponentModel;
using EtlPipelines.Abstractions;
using EtlPipelines.Transforms;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Tests.Stages;

/// <summary>A raw record as it arrives from the upstream system.</summary>
public sealed record OrderRow(int Id, string Customer, decimal Amount);

/// <summary>The shape the destination expects.</summary>
public sealed record OrderDto(int Id, string Customer, decimal AmountInCents);

/// <summary>
/// The extract port: what was previously a stage with no input and no output is now a typed source,
/// so the builder can check it lines up with whatever follows it.
/// </summary>
[Description("Downloads orders from the upstream system")]
public sealed class DownloadStage(ILogger<DownloadStage> logger) : IDataSource<OrderRow>, IAsyncInitializable
{
    private int _position;

    /// <summary>Rows this source will hand out. Set by tests.</summary>
    public IReadOnlyList<OrderRow> Rows { get; init; } = [];

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Opening download for {RowCount} orders", Rows.Count);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ErrorOr<int>> ReadAsync(Memory<OrderRow> buffer, CancellationToken cancellationToken)
    {
        var remaining = Rows.Count - _position;
        if (remaining <= 0)
        {
            return ValueTask.FromResult<ErrorOr<int>>(0);
        }

        var count = Math.Min(remaining, buffer.Length);
        for (var i = 0; i < count; i++)
        {
            buffer.Span[i] = Rows[_position + i];
        }

        _position += count;
        return ValueTask.FromResult<ErrorOr<int>>(count);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The transform port. Written per row; the framework supplies the batching loop, so nothing here
/// deals with buffers, counts or partial consumption.
/// </summary>
[Description("Converts order amounts to cents")]
public sealed class TransformStage : RowTransform<OrderRow, OrderDto>
{
    protected override ErrorOr<OrderDto> Transform(in OrderRow row) =>
        row.Amount < 0
            ? Error.Validation("order.negative_amount", $"Order {row.Id} has a negative amount.")
            : new OrderDto(row.Id, row.Customer, decimal.Round(row.Amount * 100));
}

/// <summary>
/// The load port. Implements <see cref="IAsyncCompletable"/> so the commit happens once, at the end,
/// and only when every batch landed — which disposal alone could not express.
/// </summary>
[Description("Uploads orders to the destination")]
public sealed class UploadStage(ILogger<UploadStage> logger) : IDataSink<OrderDto>, IAsyncCompletable
{
    private readonly List<OrderDto> _staged = [];

    public IReadOnlyList<OrderDto> Committed { get; private set; } = [];

    public ValueTask<ErrorOr<int>> WriteAsync(ReadOnlyMemory<OrderDto> batch, CancellationToken cancellationToken)
    {
        _staged.AddRange(batch.ToArray());
        return ValueTask.FromResult<ErrorOr<int>>(batch.Length);
    }

    public ValueTask<ErrorOr<Success>> CompleteAsync(CancellationToken cancellationToken)
    {
        Committed = _staged.ToArray();
        logger.LogInformation("Committed {RowCount} orders", Committed.Count);
        return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
