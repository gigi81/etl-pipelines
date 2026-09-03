using System.Collections.Concurrent;
using System.Diagnostics;
using EtlPipelines.Core;
using EtlPipelines.PipelineExecution.V1;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.GrpcClient;

/// <summary>
/// Forwards the runtime's own traces to <see cref="PipelineExecutionService"/> - one
/// <c>ReportStageResult</c> call per completed stage, one <c>ReportRunResult</c> call for the run
/// itself. The same <see cref="ActivityListener"/> subscription against
/// <see cref="EtlDiagnostics.SourceName"/> that <c>EtlPipelines.Hosting.PipelineTraceLogger</c>
/// already makes, with a gRPC sink instead of a log line (SERVER.md Phase 6).
/// </summary>
/// <remarks>
/// Listening starts in the constructor - resolving this from the container (which
/// <c>EtlPipelines.Hosting.EtlPipelinesHost</c> does by default for every application built on
/// it) is what turns it on, the same contract <c>PipelineTraceLogger</c> already has.
/// <para>
/// <see cref="EtlDiagnostics"/> tags a stage's span with its own row counts
/// (<see cref="EtlDiagnostics.RowsIn"/>/<see cref="EtlDiagnostics.RowsOut"/>/
/// <see cref="EtlDiagnostics.RowsFailed"/>) but never aggregates them onto the run's own span -
/// that aggregation (<c>PipelineResult.FromStages</c>) lives in <c>EtlPipelines.Core</c>, over a
/// <c>PipelineResult</c> this listener never sees. Reproducing it here, from each run's own
/// buffered stage tags, is what lets <c>ReportRunResultRequest.rows_read/rows_written/rows_failed</c>
/// be filled in without any change to <c>EtlPipeline.RunAsync</c> itself.
/// </para>
/// </remarks>
public sealed class GrpcProgressReporter : IDisposable
{
    private readonly PipelineExecutionService.PipelineExecutionServiceClient _client;
    private readonly string _sessionId;
    private readonly ILogger<GrpcProgressReporter> _logger;
    private readonly ActivityListener _listener;

    // Keyed by PipelineContext.RunId (EtlDiagnostics.RunId), the same tag both a run's span and
    // every stage span inside it carry - buffered until the run's own span stops, so
    // ReportRunResult can total what every stage reported the same way PipelineResult.FromStages
    // does (see the class remarks above).
    private readonly ConcurrentDictionary<Guid, List<StageTotals>> _runStages = new();

    // One counter for this reporter's whole process lifetime, not reset per PipelineContext.RunId:
    // `run` with no pipeline name executes every registered pipeline in one process, each getting
    // its own internal RunId, but every one of them reports under the same --session-id (there is
    // no per-internal-run field on the wire, only session_id) - StageResults' own
    // (RunId, Sequence) uniqueness constraint (SERVER.md Phase 3) is exactly what a per-run reset
    // would collide with.
    private int _sequence = -1;

    /// <summary>Begins listening to the runtime's activity source.</summary>
    public GrpcProgressReporter(
        PipelineExecutionService.PipelineExecutionServiceClient client, string sessionId, ILogger<GrpcProgressReporter> logger)
    {
        _client = client;
        _sessionId = sessionId;
        _logger = logger;

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EtlDiagnostics.SourceName,
            // All data rather than propagation only - the tags carry the row counts this exists to
            // forward, same reasoning as PipelineTraceLogger's own copy of this sampler.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void OnActivityStopped(Activity activity)
    {
        try
        {
            if (activity.GetTagItem(EtlDiagnostics.Stage) is string stageName)
            {
                ReportStage(activity, stageName);
            }
            else
            {
                ReportRun(activity);
            }
        }
        catch (Exception exception)
        {
            // A stalled or unreachable server should not take down the pipeline it is only meant
            // to be watching - the run already has its own real result to hand PipelineRunner
            // regardless of whether the server ever heard about it.
            _logger.LogWarning(exception, "Failed to report progress for {Activity}", activity.DisplayName);
        }
    }

    private void ReportStage(Activity activity, string stageName)
    {
        var runId = RunIdOf(activity);
        var rowsIn = LongTagOf(activity, EtlDiagnostics.RowsIn);
        var rowsOut = LongTagOf(activity, EtlDiagnostics.RowsOut);
        var rowsFailed = LongTagOf(activity, EtlDiagnostics.RowsFailed);
        var failed = activity.Status == ActivityStatusCode.Error;

        _runStages.GetOrAdd(runId, static _ => []).Add(new StageTotals(rowsIn, rowsOut, rowsFailed));

        var request = new ReportStageResultRequest
        {
            SessionId = _sessionId,
            Sequence = Interlocked.Increment(ref _sequence),
            Name = stageName,
            RowsIn = rowsIn,
            RowsOut = rowsOut,
            RowsFailed = rowsFailed,
            ElapsedMs = (long)activity.Duration.TotalMilliseconds,
            ErrorCode = failed ? StringTagOf(activity, EtlDiagnostics.ErrorCode) : string.Empty,
            ErrorDescription = failed ? activity.StatusDescription ?? string.Empty : string.Empty,
        };

        _client.ReportStageResult(request);
    }

    private void ReportRun(Activity activity)
    {
        var runId = RunIdOf(activity);
        var stages = _runStages.TryRemove(runId, out var found) ? found : [];
        var succeeded = activity.Status != ActivityStatusCode.Error;

        var firstMoved = stages.FirstOrDefault(MovedRows);
        var lastMoved = stages.LastOrDefault(MovedRows);

        var request = new ReportRunResultRequest
        {
            SessionId = _sessionId,
            Outcome = succeeded ? ReportRunResultRequest.Types.Outcome.Succeeded : ReportRunResultRequest.Types.Outcome.Failed,
            // Mirrors PipelineRunner.RunAsync's own exit-code contract (0 succeeded, 1 failed) -
            // the process this reporter runs inside returns exactly that, independently, so this
            // is what the server should agree the run's outcome was too.
            ExitCode = succeeded ? 0 : 1,
            RowsRead = firstMoved.RowsIn,
            RowsWritten = lastMoved.RowsOut,
            RowsFailed = stages.Sum(stage => stage.RowsFailed),
        };

        _client.ReportRunResult(request);
    }

    private static bool MovedRows(StageTotals stage) => stage.RowsIn > 0 || stage.RowsOut > 0 || stage.RowsFailed > 0;

    private static Guid RunIdOf(Activity activity) => activity.GetTagItem(EtlDiagnostics.RunId) is Guid runId ? runId : Guid.Empty;

    private static long LongTagOf(Activity activity, string tag) => activity.GetTagItem(tag) is long value ? value : 0;

    private static string StringTagOf(Activity activity, string tag) => activity.GetTagItem(tag) as string ?? string.Empty;

    private readonly record struct StageTotals(long RowsIn, long RowsOut, long RowsFailed);
}
