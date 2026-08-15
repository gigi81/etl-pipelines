namespace EtlPipelines.Abstractions.Configuration;

/// <summary>Tuning knobs for a pipeline run.</summary>
public sealed class PipelineOptions
{
    /// <summary>
    /// Rows per batch buffer. Larger batches amortise per-call overhead across more rows; smaller
    /// batches lower latency and peak memory. Peak buffer memory for a streaming pipeline is roughly
    /// <c>BatchSize × (ChannelCapacity + 2) × sizeof(row)</c> per stage boundary.
    /// </summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>
    /// Batches in flight between adjacent stages. This is what produces back-pressure: once the
    /// channel is full the upstream stage blocks, so a slow sink throttles the source instead of
    /// letting unbounded batches pile up in memory. Capacity above one is what lets extract,
    /// transform and load actually overlap.
    /// </summary>
    public int ChannelCapacity { get; set; } = 4;

    /// <summary>How to handle a rejected row. Defaults to <see cref="RowErrorAction.Fail"/>.</summary>
    public RowErrorAction OnRowError { get; set; } = RowErrorAction.Fail;

    /// <summary>
    /// Rejected rows tolerated before the run fails anyway, when <see cref="OnRowError"/> is not
    /// <see cref="RowErrorAction.Fail"/>. Zero means unlimited — the tolerance is already expressed
    /// by the action itself.
    /// </summary>
    public long MaxRowErrors { get; set; }

    /// <summary>
    /// Throws if the options cannot produce a working pipeline.
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ChannelCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRowErrors);
    }
}
