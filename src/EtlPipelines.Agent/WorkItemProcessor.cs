using EtlPipelines.AgentExecution.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent;

/// <summary>
/// Carries out the work items the Server sends this agent - installing a package, running a pipeline - and
/// reports how each went. Items are handled at the same time, in the background (<see cref="WorkScheduler"/>),
/// so the stream of work items is never held up by one of them; they belong to the agent, not to the connection
/// they arrived on, so a run carries on when the connection to the Server is lost and made again.
/// </summary>
public sealed class WorkItemProcessor : IWorkItemProcessor
{
    /// <summary>How many times telling the Server a run started or ended is tried before giving up.</summary>
    private const int MaxReportAttempts = 8;

    private static readonly TimeSpan MaxReportDelay = TimeSpan.FromSeconds(30);

    private readonly AgentService.AgentServiceClient _client;
    private readonly PackageInstaller _packageInstaller;
    private readonly IOptions<AgentOptions> _options;
    private readonly ILogger<WorkItemProcessor> _logger;
    private readonly WorkScheduler _scheduler;

    /// <summary>Creates the processor.</summary>
    public WorkItemProcessor(
        AgentService.AgentServiceClient client,
        PackageInstaller packageInstaller,
        IOptions<AgentOptions> options,
        ILogger<WorkItemProcessor> logger)
    {
        _client = client;
        _packageInstaller = packageInstaller;
        _options = options;
        _logger = logger;
        _scheduler = new WorkScheduler(options.Value.MaxConcurrentRuns, logger);
    }

    /// <inheritdoc />
    /// <remarks>A run waits for a free slot (<see cref="AgentOptions.MaxConcurrentRuns"/>); an install starts straight away.</remarks>
    public void Process(string agentId, WorkItem workItem, CancellationToken cancellationToken)
    {
        switch (workItem.KindCase)
        {
            case WorkItem.KindOneofCase.InstallPackage:
                _scheduler.RunUnlimited(
                    token => HandleInstallPackageAsync(agentId, workItem.InstallPackage, workItem.WorkItemId, token), cancellationToken);
                break;

            case WorkItem.KindOneofCase.ExecutePipeline:
                if (_scheduler.Running >= _options.Value.MaxConcurrentRuns)
                {
                    _logger.LogInformation(
                        "Run {SessionId} waits for a free slot ({Running} running, {Waiting} already waiting).",
                        workItem.ExecutePipeline.SessionId, _scheduler.Running, _scheduler.Waiting);
                }

                _scheduler.RunLimited(token => HandleExecutePipelineAsync(agentId, workItem.ExecutePipeline, token), cancellationToken);
                break;

            default:
                _logger.LogWarning(
                    "Ignoring work item {WorkItemId} of kind {Kind} - not handled.", workItem.WorkItemId, workItem.KindCase);
                break;
        }
    }

    /// <inheritdoc />
    public Task WhenIdleAsync() => _scheduler.WhenIdleAsync();

    private async Task HandleInstallPackageAsync(
        string agentId, InstallPackageWorkItem install, string workItemId, CancellationToken cancellationToken)
    {
        var report = new ReportInstallResultRequest { AgentId = agentId, WorkItemId = workItemId };

        try
        {
            var shimPath = await _packageInstaller
                .InstallAsync(install.PackageId, install.Version, install.FeedUrls, cancellationToken)
                .ConfigureAwait(false);
            var pipelineNames = await PipelineProcessRunner
                .ListPipelineNamesAsync(shimPath, cancellationToken)
                .ConfigureAwait(false);

            report.Succeeded = true;
            report.PipelineNames.AddRange(pipelineNames);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately broad: whatever went wrong installing or listing this one package
            // becomes a reported failure for this one work item, not a crashed agent that can no
            // longer serve any other work.
            _logger.LogWarning(exception, "Install failed for {PackageId} {Version}", install.PackageId, install.Version);
            report.Succeeded = false;
            report.Error = exception.Message;
        }

        try
        {
            await _client.ReportInstallResultAsync(report, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
        {
            // The install call waiting for this answer lives in the Server's memory: if the Server is gone,
            // so is the question. Nothing to retry.
            _logger.LogWarning(exception, "Could not report the install of {PackageId} {Version}", install.PackageId, install.Version);
        }
    }

    /// <summary>
    /// Runs a pipeline package's shim for <paramref name="work"/> - the run
    /// itself reports its own configuration pull and progress straight to
    /// <c>PipelineExecutionService</c> (via the launched process's own <c>EtlPipelines.GrpcClient</c>,
    /// SERVER.md Phase 6), so this only ever tells the server when the process started and when it
    /// exited, via <c>ReportExecutionStatus</c> - a fallback signal for a run whose process never
    /// managed to report anything itself. A package version missing from this agent's cache is
    /// installed first, from the work item's feed URLs.
    /// </summary>
    private async Task HandleExecutePipelineAsync(string agentId, ExecutePipelineWorkItem work, CancellationToken cancellationToken)
    {
        try
        {
            // Not in the cache (never installed on this agent, or evicted since): install it now
            // from the feed the server sent, rather than failing a run the server had no way of
            // knowing this agent couldn't serve. No feed URLs means an older server - keep the old
            // behaviour and fail clearly.
            var shimPath = _packageInstaller.TryGetInstalledShimPath(work.PackageId, work.PackageVersion);
            if (shimPath is null && work.FeedUrls.Count > 0)
            {
                _logger.LogInformation(
                    "Installing {PackageId} {Version} on demand for session {SessionId}", work.PackageId, work.PackageVersion, work.SessionId);
                shimPath = await _packageInstaller
                    .InstallAsync(work.PackageId, work.PackageVersion, work.FeedUrls, cancellationToken)
                    .ConfigureAwait(false);
            }

            shimPath ??= _packageInstaller.GetInstalledShimPath(work.PackageId, work.PackageVersion);

            await ReportAsync(
                new ReportExecutionStatusRequest
                {
                    AgentId = agentId,
                    SessionId = work.SessionId,
                    Status = ReportExecutionStatusRequest.Types.Status.Started,
                },
                cancellationToken).ConfigureAwait(false);

            var exitCode = await PipelineProcessRunner
                .RunAsync(
                    shimPath,
                    work.PipelineName,
                    work.SessionId,
                    work.ServerUrl,
                    _options.Value.ResourceSampleInterval,
                    (sample, token) => ReportResourceSampleAsync(agentId, work.SessionId, sample, token),
                    cancellationToken)
                .ConfigureAwait(false);

            await ReportAsync(
                new ReportExecutionStatusRequest
                {
                    AgentId = agentId,
                    SessionId = work.SessionId,
                    Status = ReportExecutionStatusRequest.Types.Status.Exited,
                    ExitCode = exitCode,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately broad, same reasoning as HandleInstallPackageAsync's own catch: this
            // work item's own failure (the package isn't in this agent's cache, the shim could
            // not be started) never takes down the agent's ability to serve other work. Reported
            // as an EXITED(-1) rather than left silent, so the run does not sit "Dispatched"
            // forever with nobody ever having told the server it will never report in.
            _logger.LogWarning(
                exception, "Execution failed for pipeline {PipelineName} ({PackageId} {Version})",
                work.PipelineName, work.PackageId, work.PackageVersion);

            try
            {
                await ReportAsync(
                    new ReportExecutionStatusRequest
                    {
                        AgentId = agentId,
                        SessionId = work.SessionId,
                        Status = ReportExecutionStatusRequest.Types.Status.Exited,
                        ExitCode = -1,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reportException) when (reportException is not OperationCanceledException)
            {
                _logger.LogWarning(reportException, "Failed to report execution failure for session {SessionId}", work.SessionId);
            }
        }
    }

    /// <summary>
    /// Tells the Server a run started or ended, trying again while it cannot be reached: a Server that is
    /// restarting is gone for a few seconds, and a run that finished meanwhile must still end up reported.
    /// Gives up after <see cref="MaxReportAttempts"/> attempts.
    /// </summary>
    private async Task ReportAsync(ReportExecutionStatusRequest request, CancellationToken cancellationToken)
    {
        var delay = _options.Value.ReportRetryDelay;

        for (var attempt = 1; attempt <= MaxReportAttempts; attempt++)
        {
            var failure = await TryReportAsync(request, cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                return;
            }

            if (attempt == MaxReportAttempts)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }

            _logger.LogWarning(
                "Could not report {Status} for session {SessionId} ({Reason}); trying again in {Delay}.",
                request.Status, request.SessionId, failure.Status.Detail, delay);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReportDelay.Ticks));
        }
    }

    /// <summary>One attempt at <see cref="ReportAsync"/>: null when it got through, the failure when the Server could not be reached.</summary>
    private async Task<RpcException?> TryReportAsync(ReportExecutionStatusRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ReportExecutionStatusAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
        {
            return exception;
        }
    }

    private async Task ReportResourceSampleAsync(string agentId, string sessionId, ResourceSample sample, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ReportExecutionStatusAsync(
                new ReportExecutionStatusRequest
                {
                    AgentId = agentId,
                    SessionId = sessionId,
                    Status = ReportExecutionStatusRequest.Types.Status.ResourceSample,
                    CpuPercent = sample.CpuPercent,
                    WorkingSetBytes = sample.WorkingSetBytes,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A lost sample is only a gap in a chart - never a reason to fail the run itself.
            _logger.LogDebug(exception, "Failed to report a resource sample for session {SessionId}", sessionId);
        }
    }
}
