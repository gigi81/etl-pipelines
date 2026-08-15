using System.Diagnostics;
using System.Diagnostics.Metrics;
using EtlPipelines.Abstractions;

namespace EtlPipelines;

/// <summary>
/// Tracing and metrics for pipeline runs, exposed through the standard .NET primitives so any
/// OpenTelemetry exporter picks them up without this library depending on one.
/// </summary>
public static class EtlDiagnostics
{
    /// <summary>The name to enable when subscribing to pipeline traces.</summary>
    public const string SourceName = "EtlPipelines";

    internal static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> RowsIn =
        Meter.CreateCounter<long>("etl.rows.in", "rows", "Rows consumed by a stage.");

    private static readonly Counter<long> RowsOut =
        Meter.CreateCounter<long>("etl.rows.out", "rows", "Rows produced by a stage.");

    private static readonly Counter<long> RowsFailed =
        Meter.CreateCounter<long>("etl.rows.failed", "rows", "Rows rejected by a stage.");

    private static readonly Histogram<double> StageDuration =
        Meter.CreateHistogram<double>("etl.stage.duration", "ms", "Stage wall-clock duration.");

    internal static void RecordStage(string pipeline, StageResult stage)
    {
        var tags = new TagList
        {
            { "etl.pipeline", pipeline },
            { "etl.stage", stage.Name },
        };

        RowsIn.Add(stage.RowsIn, tags);
        RowsOut.Add(stage.RowsOut, tags);
        RowsFailed.Add(stage.RowsFailed, tags);
        StageDuration.Record(stage.Elapsed.TotalMilliseconds, tags);
    }
}
