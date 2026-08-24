using System.Diagnostics;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core.Tests.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Core.Tests;

/// <summary>
/// Coarse stages published no span and no metric before <see cref="EtlPipeline"/> started opening
/// one span per stage itself. These tests listen the same way an OpenTelemetry exporter would, and
/// pin the two things that change: every stage - coarse or dataflow - now gets exactly one span, and
/// a stage that fails still reports what it moved.
/// </summary>
public sealed class StageTelemetryTests : IDisposable
{
    private const string PipelineName = "telemetry";

    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;

    public StageTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EtlDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // The listener is process-wide and every other test in this assembly runs pipelines of
            // its own on the same ActivitySource. Filtering to this test's own pipeline name is what
            // keeps a concurrently-running SamplePipelinesTest from polluting these assertions.
            ActivityStopped = activity =>
            {
                if ((string?)activity.GetTagItem("etl.pipeline") == PipelineName)
                {
                    _activities.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Test]
    [NotInParallel]
    public async Task Traces_a_coarse_stage_that_previously_published_nothing()
    {
        //arrange
        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .AddStage("fetch", (_, _) => ValueTask.FromResult<ErrorOr<Success>>(Result.Success))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();

        var stage = _activities.Should().ContainSingle(a => a.OperationName == "etl.stage fetch").Subject;
        stage.Tags.Should().Contain(t => t.Key == "etl.stage" && t.Value == "fetch");
        stage.Status.Should().Be(ActivityStatusCode.Unset, "an unset status is how a span reports success");
    }

    [Test]
    [NotInParallel]
    public async Task Records_a_failed_coarse_stage_with_an_error_status()
    {
        //arrange
        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .AddStage("fetch", (_, _) =>
                ValueTask.FromResult<ErrorOr<Success>>(Error.Failure("fetch.failed", "no network")))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();

        var stage = _activities.Should().ContainSingle(a => a.OperationName == "etl.stage fetch").Subject;
        stage.Status.Should().Be(ActivityStatusCode.Error);
        stage.StatusDescription.Should().Be("no network");
    }

    [Test]
    [NotInParallel]
    public async Task Traces_a_dataflow_stage_exactly_once()
    {
        //arrange
        // DataflowStage used to open its own span underneath this loop's. Two listeners for the same
        // stage would mean the fix regressed into double counting instead of fixing the gap.
        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .From(new DownloadStage(NullLogger<DownloadStage>.Instance))
            .Through(new TransformStage())
            .To(new UploadStage(NullLogger<UploadStage>.Instance))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();

        var stageSpans = _activities.Where(a => a.OperationName.StartsWith("etl.stage ", StringComparison.Ordinal));
        stageSpans.Should().ContainSingle("one dataflow stage must produce one span, not one from the loop and one from the stage");
    }

    [Test]
    [NotInParallel]
    public async Task Reports_the_rows_a_dataflow_stage_moved_before_it_failed()
    {
        //arrange
        // The row counts a failed DataflowStage collected have nowhere to go except through the
        // error it returns - this is the case EtlDiagnostics.WithStageResult exists for.
        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .From(new DownloadStage(NullLogger<DownloadStage>.Instance)
            {
                Rows = [new OrderRow(1, "acme", -5m)],
            })
            .Through(new TransformStage())
            .To(new UploadStage(NullLogger<UploadStage>.Instance))
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();

        var stage = _activities.Should().ContainSingle(a => a.OperationName.StartsWith("etl.stage ", StringComparison.Ordinal)).Subject;
        stage.Status.Should().Be(ActivityStatusCode.Error);
        stage.TagObjects.Should().Contain(t => t.Key == "etl.rows.in" && Equals(t.Value, 1L),
            "the row that reached the source before the transform rejected it was still consumed");
    }

    [Test]
    [NotInParallel]
    public async Task Traces_a_renamed_stage_under_its_public_name()
    {
        //arrange
        // AddStage<T>(name) wraps the stage in RenamedStage. The span is opened from stage.Name
        // before ExecuteAsync runs, so the inner StageResult.Name must be rewritten to match, or the
        // span and the metric tag disagree about what the stage is called.
        var services = new ServiceCollection();
        services.AddEtlPipeline(PipelineName, builder => builder.AddStage<NamedStage>("public-name"));

        var provider = services.BuildServiceProvider();

        //act
        var result = await provider.GetRequiredEtlPipeline(PipelineName).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be("public-name");

        var stage = _activities.Should().ContainSingle(a => a.OperationName == "etl.stage public-name").Subject;
        stage.Tags.Should().Contain(t => t.Key == "etl.stage" && t.Value == "public-name");
    }

    /// <summary>A stage whose own <see cref="Name"/> differs from the one it is renamed to in a test.</summary>
    private sealed class NamedStage : IPipelineStage
    {
        public string Name => "internal-name";

        public ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ErrorOr<StageResult>>(new StageResult(Name, 0, 0, 0, TimeSpan.Zero));
    }
}
