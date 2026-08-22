using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Hosting;

/// <summary>
/// Writes the pipeline's own traces to the log for as long as it is alive.
/// </summary>
/// <remarks>
/// The runtime reports itself through <see cref="ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/> rather than through <c>ILogger</c>, so that an
/// application can point OpenTelemetry at it without the library depending on any of it. The cost of
/// that choice is that nothing appears until somebody listens, which is what this does — and a
/// command line application that has no exporter still wants to see where its time went.
/// <para>
/// Listening starts in the constructor, so resolving this from the container is what turns it on.
/// Nothing is logged unless <see cref="LogLevel.Debug"/> is enabled.
/// </para>
/// </remarks>
public sealed class PipelineTraceLogger : IDisposable
{
    /// <summary>The activity source name the runtime publishes under.</summary>
    private const string SourceName = "EtlPipelines";

    private readonly ILogger<PipelineTraceLogger> _logger;
    private readonly ActivityListener _listener;

    /// <summary>Begins listening to the runtime's activity source.</summary>
    public PipelineTraceLogger(ILogger<PipelineTraceLogger> logger)
    {
        _logger = logger;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            // All data rather than propagation only: the tags carry the row counts, and without them
            // there would be nothing worth logging.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = Log,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private void Log(Activity activity)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "{Activity} took {Elapsed:F0} ms{Tags}",
            activity.DisplayName,
            activity.Duration.TotalMilliseconds,
            string.Concat(activity.TagObjects.Select(tag => $" {tag.Key}={tag.Value}")));
    }
}
