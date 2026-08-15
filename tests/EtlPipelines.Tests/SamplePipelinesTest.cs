using FluentAssertions;
using EtlPipelines.Abstractions;
using EtlPipelines.Tests.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Tests;

/// <summary>
/// The end-to-end shape a user actually writes: a typed dataflow composed of the three sample ports.
/// </summary>
public class SamplePipelinesTest
{
    private static readonly OrderRow[] Orders =
    [
        new(1, "acme", 10.00m),
        new(2, "globex", 25.50m),
        new(3, "initech", 3.99m),
    ];

    [Fact]
    public async Task Runs_a_source_transform_sink_dataflow()
    {
        var upload = new UploadStage(NullLogger<UploadStage>.Instance);

        var pipeline = EtlPipeline.CreateBuilder("orders")
            .From<OrderRow>(new DownloadStage(NullLogger<DownloadStage>.Instance) { Rows = Orders })
            .Through(new TransformStage())
            .To(upload)
            .Build();

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(3);
        result.Value.RowsWritten.Should().Be(3);
        result.Value.RowsFailed.Should().Be(0);

        upload.Committed.Should().BeEquivalentTo(
        [
            new OrderDto(1, "acme", 1000m),
            new OrderDto(2, "globex", 2550m),
            new OrderDto(3, "initech", 399m),
        ]);
    }

    [Fact]
    public async Task Resolves_ports_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IDataSource<OrderRow>>(sp =>
            new DownloadStage(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DownloadStage>>())
            {
                Rows = Orders,
            });
        services.AddSingleton<IDataTransform<OrderRow, OrderDto>, TransformStage>();
        services.AddSingleton<IDataSink<OrderDto>, UploadStage>();

        // Only the row types are named; To() infers completely.
        services.AddEtlPipeline("orders", builder => builder
            .From<OrderRow>()
            .Through<OrderDto>()
            .To());

        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IPipelineFactory>().Get("orders");

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Value.RowsWritten.Should().Be(3);
        result.Value.Stages.Should().ContainSingle();
    }

    [Fact]
    public async Task Rejects_a_bad_row_and_fails_the_run_by_default()
    {
        var pipeline = EtlPipeline.CreateBuilder("orders")
            .From<OrderRow>(new DownloadStage(NullLogger<DownloadStage>.Instance)
            {
                Rows = [new OrderRow(1, "acme", -5m)],
            })
            .Through(new TransformStage())
            .To(new UploadStage(NullLogger<UploadStage>.Instance))
            .Build();

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("order.negative_amount");
    }

    [Fact]
    public void Refuses_to_build_a_pipeline_with_no_stages()
    {
        var act = () => EtlPipeline.CreateBuilder("empty").Build();

        act.Should().Throw<InvalidOperationException>().WithMessage("*no stages*");
    }
}
