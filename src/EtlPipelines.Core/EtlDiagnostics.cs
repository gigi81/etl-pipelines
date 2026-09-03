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

    private const string Prefix = "etl";

    // Namespaces. Each names a span and a tag on that span, and prefixes the instruments measuring
    // it — deliberately the same word in all three, which is what lets a trace and a metric be lined
    // up against each other.
    private const string Pipeline = $"{Prefix}.pipeline";

    /// <summary>
    /// Tag key holding a stage's name, set on every stage span (never on the top-level run span) -
    /// the one tag that tells an external <see cref="ActivityListener"/> it is looking at a stage
    /// span rather than the run span. <c>EtlPipelines.GrpcClient</c>'s <c>GrpcProgressReporter</c>
    /// (SERVER.md Phase 6) reads it for exactly that, rather than duplicating this literal string
    /// of its own.
    /// </summary>
    public const string Stage = $"{Prefix}.stage";

    private const string Rows = $"{Prefix}.rows";

    // Dot-separated throughout, per OpenTelemetry's attribute naming: the dot is what namespaces a
    // name, and a quantity that is both counted and put on a span should be called the same thing in
    // each place rather than etl.rows.in in one and etl.rows_in in the other.

    /// <summary>Tag key holding the rows a stage consumed, set on every stage span.</summary>
    public const string RowsIn = $"{Rows}.in";

    /// <summary>Tag key holding the rows a stage produced, set on every stage span.</summary>
    public const string RowsOut = $"{Rows}.out";

    /// <summary>Tag key holding the rows a stage rejected, set on every stage span.</summary>
    public const string RowsFailed = $"{Rows}.failed";

    private const string StageDuration = $"{Stage}.duration";
    private const string RunDuration = $"{Pipeline}.duration";
    private const string Runs = $"{Pipeline}.runs";

    /// <summary>
    /// Tag key holding the run's id, set on both the run span and every stage span it contains -
    /// what correlates a stage's tags back to the run they belong to.
    /// </summary>
    public const string RunId = $"{Prefix}.run.id";

    private const string Outcome = $"{Prefix}.outcome";

    /// <summary>
    /// Tag key holding a failed span's <c>Error.Code</c> - the one piece of a failure the span
    /// does not already expose through <see cref="Activity.Status"/>/
    /// <see cref="Activity.StatusDescription"/> (which <see cref="Fail"/> sets to the error's
    /// description). Only present when the span failed.
    /// </summary>
    public const string ErrorCode = $"{Prefix}.error.code";

    // Not a published name - carried on Error.Metadata only, between DataflowStage and the run loop
    // that now records every stage. Never appears on a span or an instrument.
    private const string StageResultKey = $"{Stage}.result";

    private const string Succeeded = "succeeded";
    private const string Failed = "failed";

    private static readonly ActivitySource ActivitySource = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> RowsInCounter =
        Meter.CreateCounter<long>(RowsIn, "rows", "Rows consumed by a stage.");

    private static readonly Counter<long> RowsOutCounter =
        Meter.CreateCounter<long>(RowsOut, "rows", "Rows produced by a stage.");

    private static readonly Counter<long> RowsFailedCounter =
        Meter.CreateCounter<long>(RowsFailed, "rows", "Rows rejected by a stage.");

    private static readonly Histogram<double> StageDurationHistogram =
        Meter.CreateHistogram<double>(StageDuration, "ms", "Stage wall-clock duration.");

    private static readonly Histogram<double> RunDurationHistogram =
        Meter.CreateHistogram<double>(RunDuration, "ms", "Pipeline wall-clock duration.");

    private static readonly Counter<long> RunsCounter =
        Meter.CreateCounter<long>(Runs, "runs", "Pipeline runs, tagged by outcome.");

    /// <summary>Begins the span covering one run.</summary>
    internal static Activity? StartRun(PipelineContext context)
    {
        var activity = ActivitySource.StartActivity($"{Pipeline} {context.PipelineName}");

        activity?.SetTag(Pipeline, context.PipelineName);
        activity?.SetTag(RunId, context.RunId);

        return activity;
    }

    /// <summary>Begins the span covering one stage of a run.</summary>
    internal static Activity? StartStage(PipelineContext context, string stage)
    {
        var activity = ActivitySource.StartActivity($"{Stage} {stage}");

        activity?.SetTag(Pipeline, context.PipelineName);
        activity?.SetTag(Stage, stage);
        activity?.SetTag(RunId, context.RunId);

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
            { Pipeline, context.PipelineName },
            { Stage, result.Name },
            { Outcome, OutcomeOf(error) },
        };

        RowsInCounter.Add(result.RowsIn, tags);
        RowsOutCounter.Add(result.RowsOut, tags);
        RowsFailedCounter.Add(result.RowsFailed, tags);
        StageDurationHistogram.Record(result.Elapsed.TotalMilliseconds, tags);

        activity?.SetTag(RowsIn, result.RowsIn);
        activity?.SetTag(RowsOut, result.RowsOut);
        activity?.SetTag(RowsFailed, result.RowsFailed);

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
            { Pipeline, context.PipelineName },
            { Outcome, OutcomeOf(error) },
        };

        RunDurationHistogram.Record(elapsed.TotalMilliseconds, tags);
        RunsCounter.Add(1, tags);

        Fail(activity, error);
    }

    /// <summary>
    /// Attaches what a stage moved to an error it is also returning, so a stage that fails part-way
    /// through does not lose the rows it did move before the run loop records them.
    /// </summary>
    internal static Error WithStageResult(this Error error, StageResult result) =>
        Error.Custom(
            error.NumericType,
            error.Code,
            error.Description,
            new Dictionary<string, object>(error.Metadata ?? new Dictionary<string, object>())
            {
                [StageResultKey] = result,
            });

    /// <summary>Recovers a <see cref="StageResult"/> a stage attached with <see cref="WithStageResult"/>.</summary>
    internal static StageResult? StageResultOf(Error error) =>
        error.Metadata?.GetValueOrDefault(StageResultKey) as StageResult;

    private static string OutcomeOf(Error? error) => error is null ? Succeeded : Failed;

    private static void Fail(Activity? activity, Error? error)
    {
        if (error is { } failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure.Description);
            activity?.SetTag(ErrorCode, failure.Code);
        }
    }
}
