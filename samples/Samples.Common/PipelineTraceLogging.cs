using System.Diagnostics;
using EtlPipelines.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.Common;

/// <summary>
/// Writes the pipeline's own traces to the log.
/// </summary>
/// <remarks>
/// The library reports itself through <see cref="System.Diagnostics.ActivitySource"/> and
/// <see cref="System.Diagnostics.Metrics.Meter"/> rather than through <c>ILogger</c>, so that an
/// application can point OpenTelemetry at it without the library taking a dependency on any of it.
/// The cost of that choice is that nothing appears in the console until somebody listens, which is
/// what this does — about thirty lines, and the same thirty lines whatever the destination.
/// </remarks>
internal sealed class PipelineTraceLogging(ILogger<PipelineTraceLogging> logger) : IHostedService
{
    private ActivityListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EtlDiagnostics.SourceName,
            // All data, rather than propagation only: the tags carry the row counts, and without them
            // there would be nothing worth logging.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = Log,
        };

        ActivitySource.AddActivityListener(_listener);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener?.Dispose();
        _listener = null;

        return Task.CompletedTask;
    }

    private void Log(Activity activity)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        logger.LogDebug(
            "{Activity} took {Elapsed:F0} ms{Tags}",
            activity.DisplayName,
            activity.Duration.TotalMilliseconds,
            string.Concat(activity.TagObjects.Select(tag => $" {tag.Key}={tag.Value}")));
    }
}
