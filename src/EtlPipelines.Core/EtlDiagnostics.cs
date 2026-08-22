using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EtlPipelines.Core;

/// <summary>
/// Tracing and metrics for pipeline runs, exposed through the standard .NET primitives so any
/// OpenTelemetry exporter picks them up without this library depending on one.
/// </summary>
/// <remarks>
/// Every span, instrument and tag name the runtime publishes is declared here and nowhere else. The
/// engine calls in at the points where something worth recording happened and passes what it knows;
/// what that becomes — which tag, which counter, whether a span is marked failed — is this class's
/// business alone. Keeping it that way is what makes the published telemetry reviewable in one place
/// instead of assembled from tags set across the runtime.
/// </remarks>
public static class EtlDiagnostics
{
    /// <summary>The name to enable when subscribing to pipeline traces.</summary>
    public const string SourceName = "EtlPipelines";

    private const string PipelineTag = "etl.pipeline";
    private const string StageTag = "etl.stage";
    private const string RunTag = "etl.run_id";
    private const string OutcomeTag = "etl.outcome";
    private const string RowsInTag = "etl.rows_in";
    private const string RowsOutTag = "etl.rows_out";

    private static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> RowsIn =
        Meter.CreateCounter<long>("etl.rows.in", "rows", "Rows consumed by a stage.");

    private static readonly Counter<long> RowsOut =
        Meter.CreateCounter<long>("etl.rows.out", "rows", "Rows produced by a stage.");

    private static readonly Counter<long> RowsFailed =
        Meter.CreateCounter<long>("etl.rows.failed", "rows", "Rows rejected by a stage.");

    private static readonly Histogram<double> StageDuration =
        Meter.CreateHistogram<double>("etl.stage.duration", "ms", "Stage wall-clock duration.");

    private static readonly Histogram<double> RunDuration =
        Meter.CreateHistogram<double>("etl.pipeline.duration", "ms", "Pipeline wall-clock duration.");

    private static readonly Counter<long> Runs =
        Meter.CreateCounter<long>("etl.pipeline.runs", "runs", "Pipeline runs, tagged by outcome.");

    /// <summary>Begins the span covering one run.</summary>
    internal static Activity? StartRun(PipelineContext context)
    {
        var activity = ActivitySource.StartActivity($"etl.pipeline {context.PipelineName}");

        activity?.SetTag(PipelineTag, context.PipelineName);
        activity?.SetTag(RunTag, context.RunId);

        return activity;
    }

    /// <summary>Begins the span covering one stage of a run.</summary>
    internal static Activity? StartStage(PipelineContext context, string stage)
    {
        var activity = ActivitySource.StartActivity($"etl.stage {stage}");

        activity?.SetTag(PipelineTag, context.PipelineName);
        activity?.SetTag(StageTag, stage);
        activity?.SetTag(RunTag, context.RunId);

        return activity;
    }

    /// <summary>
    /// Records what a stage did, whether or not it finished.
    /// </summary>
    /// <remarks>
    /// The rows are recorded whether the stage failed or not: one that died after half a million rows
    /// still consumed them, and a counter that only moved on success would report a load as having
    /// done nothing at all. The outcome is a tag, so the two are still tellable apart.
    /// </remarks>
    internal static void RecordStage(
        PipelineContext context,
        Activity? activity,
        StageResult result,
        Error? error = null)
    {
        var tags = new TagList
        {
            { PipelineTag, context.PipelineName },
            { StageTag, result.Name },
            { OutcomeTag, Outcome(error) },
        };

        RowsIn.Add(result.RowsIn, tags);
        RowsOut.Add(result.RowsOut, tags);
        RowsFailed.Add(result.RowsFailed, tags);
        StageDuration.Record(result.Elapsed.TotalMilliseconds, tags);

        activity?.SetTag(RowsInTag, result.RowsIn);
        activity?.SetTag(RowsOutTag, result.RowsOut);

        Fail(activity, error);
    }

    /// <summary>Records how a run ended, whether or not it finished.</summary>
    internal static void RecordRun(
        PipelineContext context,
        Activity? activity,
        TimeSpan elapsed,
        Error? error = null)
    {
        var tags = new TagList
        {
            { PipelineTag, context.PipelineName },
            { OutcomeTag, Outcome(error) },
        };

        RunDuration.Record(elapsed.TotalMilliseconds, tags);
        Runs.Add(1, tags);

        Fail(activity, error);
    }

    private static string Outcome(Error? error) => error is null ? "succeeded" : "failed";

    private static void Fail(Activity? activity, Error? error)
    {
        if (error is { } failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure.Description);
        }
    }
}
