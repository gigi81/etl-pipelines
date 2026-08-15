using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Tests.Stages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Tests;

/// <summary>
/// The end-to-end shape a user actually writes: a typed dataflow composed of the three sample ports.
/// </summary>
public class SamplePipelinesTest
{
    private static readonly OrderRow[] Orders = DownloadStage.SampleOrders;

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
    public async Task Declares_the_whole_pipeline_in_one_call()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

        // Naming the port types here is the registration. There is no second pass registering
        // IDataSource<OrderRow> and friends and then a third referring back to them by row type —
        // the pipeline declaration is the single place the composition lives.
        services.AddEtlPipeline("orders", builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredEtlPipeline("orders");

        var result = await pipeline.RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(3);
        result.Value.RowsWritten.Should().Be(3);
        result.Value.Stages.Should().ContainSingle();
    }

    [Fact]
    public async Task Injects_constructor_dependencies_into_ports_it_constructs()
    {
        // The ports are never registered, but DownloadStage and UploadStage both take an
        // ILogger<T> — resolved from the container like any other constructor dependency.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddEtlPipeline("orders", builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        var act = async () => await provider.GetRequiredEtlPipeline("orders")
            .RunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Builds_stateful_ports_fresh_for_every_run()
    {
        // A source tracks its read position. Were the pipeline to reuse one instance, the second run
        // would resume past the end and read nothing — so each run gets its own.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddEtlPipeline("orders", builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var pipeline = services.BuildServiceProvider()
            .GetRequiredEtlPipeline("orders");

        var first = await pipeline.RunAsync(CancellationToken.None);
        var second = await pipeline.RunAsync(CancellationToken.None);

        first.Value.RowsWritten.Should().Be(3);
        second.Value.RowsWritten.Should().Be(3, "the second run must not inherit the first run's position");
    }

    [Fact]
    public async Task Still_resolves_ports_the_container_owns()
    {
        // The other half of the split: when something else already registers the port — a shared
        // connection pool, a port configured elsewhere — name only the row type and the container
        // keeps ownership of the lifetime.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataSource<OrderRow>>(sp =>
            new DownloadStage(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DownloadStage>>())
            {
                Rows = Orders.Take(2).ToArray(),
            });

        services.AddEtlPipeline("orders", builder => builder
            .From<OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredEtlPipeline("orders")
            .RunAsync(CancellationToken.None);

        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsWritten.Should().Be(2);
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
    public void Says_so_plainly_when_no_pipelines_are_registered_at_all()
    {
        var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => provider.GetRequiredEtlPipeline("orders");

        // Distinct from the wrong-name case below: forgetting AddEtlPipeline entirely is a different
        // mistake, and the container's stock "no service for type IPipelineFactory" names neither.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No ETL pipelines are registered*")
            .WithMessage("*AddEtlPipeline*");
    }

    [Fact]
    public void Lists_the_known_names_when_asked_for_one_that_does_not_exist()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddEtlPipeline("orders", builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredEtlPipeline("invoices");

        act.Should().Throw<InvalidOperationException>().WithMessage("*orders*");
    }

    [Fact]
    public void Refuses_to_build_a_pipeline_with_no_stages()
    {
        var act = () => EtlPipeline.CreateBuilder("empty").Build();

        act.Should().Throw<InvalidOperationException>().WithMessage("*no stages*");
    }
}
