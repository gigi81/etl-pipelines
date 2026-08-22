using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.Common;

/// <summary>Carries the run's outcome back out of the host, which has no return value of its own.</summary>
internal sealed class SampleRunState
{
    public PipelineResult? Result { get; set; }

    public Exception? Failure { get; set; }
}

/// <summary>
/// Runs the sample's pipeline once, reports what it did, and stops the host.
/// </summary>
/// <remarks>
/// A hosted service rather than a Main body, because that is where a pipeline belongs in a real
/// application: the container is already built, configuration and logging are already wired, and the
/// pipeline is resolved from the same provider everything else uses. A sample that new'd up its own
/// <c>ServiceCollection</c> would be demonstrating something nobody actually does.
/// </remarks>
internal sealed class SampleRunner(
    Sample sample,
    SampleWorkspace workspace,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    IHostApplicationLifetime lifetime,
    SampleRunState state) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logger = loggerFactory.CreateLogger(sample.GetType());

        try
        {
            logger.LogInformation("{Description}", sample.Description);
            logger.LogInformation("Working in {Directory}", workspace.Directory.FullName);

            await sample.PrepareAsync(workspace, stoppingToken).ConfigureAwait(false);

            var run = await services
                .GetRequiredEtlPipeline(sample.PipelineName)
                .RunAsync(stoppingToken)
                .ConfigureAwait(false);

            if (run.IsError)
            {
                foreach (var error in run.Errors)
                {
                    logger.LogError("{Code}: {Description}", error.Code, error.Description);
                }

                state.Failure = new InvalidOperationException(run.FirstError.Description);
                return;
            }

            Report(logger, run.Value);

            await sample
                .ReportAsync(new SampleOutcome(run.Value, workspace, services, logger), stoppingToken)
                .ConfigureAwait(false);

            state.Result = run.Value;
        }
        catch (Exception exception)
        {
            // Recorded rather than thrown. An exception out of ExecuteAsync tears the host down with
            // a stack trace and no exit code of its own; the caller rethrows this one where it can
            // still be caught, or turns it into an exit code.
            logger.LogError(exception, "The sample did not finish");
            state.Failure = exception;
        }
        finally
        {
            // Nothing else is keeping this host up. Without this it would sit waiting for Ctrl+C.
            lifetime.StopApplication();
        }
    }

    private static void Report(ILogger logger, PipelineResult result)
    {
        logger.LogInformation(
            "{Pipeline}: read {RowsRead}, wrote {RowsWritten}, failed {RowsFailed}, in {Elapsed:F0} ms",
            result.Name,
            result.RowsRead,
            result.RowsWritten,
            result.RowsFailed,
            result.Elapsed.TotalMilliseconds);

        foreach (var stage in result.Stages)
        {
            logger.LogInformation(
                "  stage {Stage}: {RowsIn} in, {RowsOut} out, {RowsPerSecond:F0} rows/s",
                stage.Name,
                stage.RowsIn,
                stage.RowsOut,
                stage.RowsPerSecond);
        }
    }
}
