using System.Diagnostics;
using EtlPipelines.Core;
using EtlPipelines.PipelineExecution.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EtlPipelines.GrpcClient.Tests;

/// <summary>
/// Fast test for <see cref="GrpcProgressReporter"/> against a real <see cref="ActivitySource"/> -
/// SERVER.md Phase 6's own test plan for it. Drives <see cref="EtlDiagnostics.SourceName"/>
/// directly with <see cref="EtlDiagnostics"/>'s own public tag-key constants, rather than running a
/// real <c>EtlPipelines.Core</c> pipeline end to end - the same source name and tag keys
/// <c>EtlPipeline.RunAsync</c>/<c>StageExecutor</c> actually publish, without needing a whole
/// pipeline (source, sink, dataflow) built just to exercise this listener.
/// </summary>
/// <remarks>
/// <see cref="ActivitySource"/>/<see cref="ActivityListener"/> registration is process-wide -
/// <see cref="NotInParallelAttribute"/> with a shared key keeps this class's own tests from
/// observing each other's activities, the same reasoning
/// <c>EtlPipelines.Core.Tests.DataflowRuntimeTests</c> already gives its own use of the attribute.
/// </remarks>
[Category("GrpcClient")]
[NotInParallel("GrpcProgressReporter")]
public class GrpcProgressReporterTests
{
    private static readonly ActivitySource Source = new(EtlDiagnostics.SourceName);

    [Test]
    public async Task Reports_a_succeeded_stage_then_the_succeeded_run_it_belongs_to()
    {
        //arrange
        ReportStageResultRequest? stageRequest = null;
        ReportRunResultRequest? runRequest = null;
        var client = MockClient(stage => stageRequest = stage, run => runRequest = run);

        var runId = Guid.NewGuid();

        //act
        using (var reporter = new GrpcProgressReporter(client.Object, "session-1", NullLogger<GrpcProgressReporter>.Instance))
        using (var runActivity = Source.StartActivity("etl.pipeline sales"))
        {
            runActivity?.SetTag(EtlDiagnostics.RunId, runId);

            using (var stageActivity = Source.StartActivity("etl.stage seed"))
            {
                stageActivity?.SetTag(EtlDiagnostics.Stage, "seed");
                stageActivity?.SetTag(EtlDiagnostics.RunId, runId);
                stageActivity?.SetTag(EtlDiagnostics.RowsIn, 10L);
                stageActivity?.SetTag(EtlDiagnostics.RowsOut, 9L);
                stageActivity?.SetTag(EtlDiagnostics.RowsFailed, 1L);
            }
        }

        //assert
        stageRequest.Should().NotBeNull();
        stageRequest!.SessionId.Should().Be("session-1");
        stageRequest.Sequence.Should().Be(0);
        stageRequest.Name.Should().Be("seed");
        stageRequest.RowsIn.Should().Be(10);
        stageRequest.RowsOut.Should().Be(9);
        stageRequest.RowsFailed.Should().Be(1);
        stageRequest.ErrorCode.Should().BeEmpty();

        runRequest.Should().NotBeNull();
        runRequest!.SessionId.Should().Be("session-1");
        runRequest.Outcome.Should().Be(ReportRunResultRequest.Types.Outcome.Succeeded);
        runRequest.ExitCode.Should().Be(0);
        // Mirrors PipelineResult.FromStages: the first/last stage that actually moved rows -
        // "seed" is both here, since it is the only stage.
        runRequest.RowsRead.Should().Be(10);
        runRequest.RowsWritten.Should().Be(9);
        runRequest.RowsFailed.Should().Be(1);
    }

    [Test]
    public async Task Reports_a_failed_stage_with_its_error_and_the_failed_run_it_belongs_to()
    {
        //arrange
        ReportStageResultRequest? stageRequest = null;
        ReportRunResultRequest? runRequest = null;
        var client = MockClient(stage => stageRequest = stage, run => runRequest = run);

        var runId = Guid.NewGuid();

        //act
        using (var reporter = new GrpcProgressReporter(client.Object, "session-2", NullLogger<GrpcProgressReporter>.Instance))
        using (var runActivity = Source.StartActivity("etl.pipeline sales"))
        {
            runActivity?.SetTag(EtlDiagnostics.RunId, runId);
            runActivity?.SetStatus(ActivityStatusCode.Error, "seed failed");

            using (var stageActivity = Source.StartActivity("etl.stage seed"))
            {
                stageActivity?.SetTag(EtlDiagnostics.Stage, "seed");
                stageActivity?.SetTag(EtlDiagnostics.RunId, runId);
                stageActivity?.SetTag(EtlDiagnostics.RowsIn, 5L);
                stageActivity?.SetTag(EtlDiagnostics.RowsOut, 0L);
                stageActivity?.SetTag(EtlDiagnostics.RowsFailed, 5L);
                stageActivity?.SetTag(EtlDiagnostics.ErrorCode, "Sql.ConnectionFailed");
                stageActivity?.SetStatus(ActivityStatusCode.Error, "could not open connection");
            }
        }

        //assert
        stageRequest.Should().NotBeNull();
        stageRequest!.ErrorCode.Should().Be("Sql.ConnectionFailed");
        stageRequest.ErrorDescription.Should().Be("could not open connection");

        runRequest.Should().NotBeNull();
        runRequest!.Outcome.Should().Be(ReportRunResultRequest.Types.Outcome.Failed);
        runRequest.ExitCode.Should().Be(1);
    }

    private static Mock<PipelineExecutionService.PipelineExecutionServiceClient> MockClient(
        Action<ReportStageResultRequest> onStage, Action<ReportRunResultRequest> onRun)
    {
        var client = new Mock<PipelineExecutionService.PipelineExecutionServiceClient>();

        client
            .Setup(c => c.ReportStageResult(
                It.IsAny<ReportStageResultRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ReportStageResultRequest, Metadata, DateTime?, CancellationToken>((request, _, _, _) => onStage(request))
            .Returns(new Ack());

        client
            .Setup(c => c.ReportRunResult(
                It.IsAny<ReportRunResultRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<ReportRunResultRequest, Metadata, DateTime?, CancellationToken>((request, _, _, _) => onRun(request))
            .Returns(new Ack());

        return client;
    }
}
