using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Core.Tests.Stages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Core.Tests;

/// <summary>
/// The end-to-end shape a user actually writes: a typed dataflow composed of the three sample ports.
/// </summary>
public class SamplePipelinesTest
{
    private const string PipelineName = "orders";
    private const string OtherPipelineName = "invoices";
    private const string MarkersPipelineName = "markers";

    private static readonly OrderRow[] Orders = DownloadStage.SampleOrders;

    private static readonly List<Guid> Observed = [];

    [Test]
    public async Task Runs_a_source_transform_sink_dataflow()
    {
        //arrange
        var upload = new UploadStage(NullLogger<UploadStage>.Instance);

        var pipeline = EtlPipeline.CreateBuilder(PipelineName)
            .From(new DownloadStage(NullLogger<DownloadStage>.Instance) { Rows = Orders })
            .Through(new TransformStage())
            .To(upload)
            .Build();

        //act
        var result = await pipeline.RunAsync(CancellationToken.None);

        //assert
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

    [Test]
    public async Task Declares_the_whole_pipeline_in_one_call()
    {
        //arrange
        // Naming the port types here is the registration. There is no second pass registering
        // IDataSource<OrderRow> and friends and then a third referring back to them by row type —
        // the pipeline declaration is the single place the composition lives.
        var services = NewServices();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        //act
        var result = await provider.GetRequiredEtlPipeline(PipelineName).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(3);
        result.Value.RowsWritten.Should().Be(3);
        result.Value.Stages.Should().ContainSingle();
    }

    [Test]
    public async Task Fans_the_same_rows_out_to_two_destinations()
    {
        //arrange
        // The canonical branching shape: archive the raw record, and in parallel reshape it for the
        // real load. The source is read once, not once per destination.
        var services = NewServices();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Branch(
                b1 => b1.To<ArchiveStage>(),
                b2 => b2.Through<TransformStage, OrderDto>()
                        .To<UploadStage>()));

        var provider = services.BuildServiceProvider();

        //act
        var result = await provider.GetRequiredEtlPipeline(PipelineName).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(3, "the source is read once however many branches there are");
        result.Value.RowsWritten.Should().Be(6, "three rows reached each of the two destinations");
        result.Value.Stages.Should().ContainSingle("a fan-out is still one dataflow");
    }

    [Test]
    public async Task Injects_constructor_dependencies_into_ports_it_constructs()
    {
        //arrange
        // The ports are never registered, but DownloadStage and UploadStage both take an
        // ILogger<T> — resolved from the container like any other constructor dependency.
        var services = NewServices();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        //act
        var act = async () => await provider.GetRequiredEtlPipeline(PipelineName)
            .RunAsync(CancellationToken.None);

        //assert
        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task Builds_stateful_ports_fresh_for_every_run()
    {
        //arrange
        // A source tracks its read position. Were the pipeline to reuse one instance, the second run
        // would resume past the end and read nothing — so each run gets its own.
        var services = NewServices();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var pipeline = services.BuildServiceProvider().GetRequiredEtlPipeline(PipelineName);

        //act
        var first = await pipeline.RunAsync(CancellationToken.None);
        var second = await pipeline.RunAsync(CancellationToken.None);

        //assert
        first.Value.RowsWritten.Should().Be(3);
        second.Value.RowsWritten.Should().Be(3, "the second run must not inherit the first run's position");
    }

    [Test]
    public async Task Still_resolves_ports_the_container_owns()
    {
        //arrange
        // The other half of the split: when something else already registers the port — a shared
        // connection pool, a port configured elsewhere — name only the row type and the container
        // keeps ownership of the lifetime.
        var services = NewServices();
        services.AddScoped<IDataSource<OrderRow>>(sp =>
            new DownloadStage(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DownloadStage>>())
            {
                Rows = Orders.Take(2).ToArray(),
            });

        services.AddEtlPipeline(PipelineName, builder => builder
            .From<OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        //act
        var result = await provider.GetRequiredEtlPipeline(PipelineName).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsWritten.Should().Be(2);
    }

    [Test]
    public async Task Rejects_a_bad_row_and_fails_the_run_by_default()
    {
        //arrange
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
        result.FirstError.Code.Should().Be("order.negative_amount");
    }

    [Test]
    public void Registers_every_named_component_as_scoped_and_keyed()
    {
        //arrange
        var services = NewServices();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        //act
        var ports = services
            .Where(d => d.ServiceType == typeof(IDataSource<OrderRow>)
                     || d.ServiceType == typeof(IDataTransform<OrderRow, OrderDto>)
                     || d.ServiceType == typeof(IDataSink<OrderDto>))
            .ToArray();

        //assert
        ports.Should().HaveCount(3, "composing the pipeline is what registers its components");
        ports.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped,
            "scoped is what gives each run its own instances from the scope the run creates");
        ports.Should().OnlyContain(d => d.IsKeyedService,
            "keying by pipeline stops two pipelines sharing a component type from colliding");
        ports.Should().OnlyContain(d => d.ServiceKey!.ToString()!.StartsWith($"{PipelineName}["),
            "the key carries the pipeline name it belongs to");
    }

    [Test]
    public async Task Keeps_two_pipelines_that_share_a_component_type_apart()
    {
        //arrange
        // Both register IDataSource<OrderRow> against the same type. Unkeyed, the second registration
        // would win for both and the first pipeline would silently read the wrong data.
        var services = NewServices();

        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        services.AddEtlPipeline(OtherPipelineName, builder => builder
            .From(_ => new DownloadStage(NullLogger<DownloadStage>.Instance)
            {
                Rows = Orders.Take(1).ToArray(),
            })
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        //act
        var all = await provider.GetRequiredEtlPipeline(PipelineName).RunAsync(CancellationToken.None);
        var one = await provider.GetRequiredEtlPipeline(OtherPipelineName).RunAsync(CancellationToken.None);

        //assert
        all.Value.RowsWritten.Should().Be(3);
        one.Value.RowsWritten.Should().Be(1);
    }

    [Test]
    public async Task Resolves_stages_from_the_scope_the_run_creates()
    {
        //arrange
        // A scoped dependency shared by two components must be the same object within one run and a
        // different one on the next — which is only true if the run resolves from its own scope.
        var services = new ServiceCollection();
        services.AddScoped<RunMarker>();
        services.AddEtlPipeline(MarkersPipelineName, builder => builder
            .AddStage("first", (ctx, _) =>
            {
                Observed.Add(ctx.Services.GetRequiredService<RunMarker>().Id);
                return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
            })
            .AddStage("second", (ctx, _) =>
            {
                Observed.Add(ctx.Services.GetRequiredService<RunMarker>().Id);
                return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
            }));

        var pipeline = services.BuildServiceProvider().GetRequiredEtlPipeline(MarkersPipelineName);

        //act
        var first = await pipeline.RunAsync(CancellationToken.None);
        var second = await pipeline.RunAsync(CancellationToken.None);

        //assert
        first.IsError.Should().BeFalse();
        second.IsError.Should().BeFalse();

        Observed.Should().HaveCount(4);
        Observed[0].Should().Be(Observed[1], "both stages of a run resolve from that run's one scope");
        Observed[2].Should().Be(Observed[3]);
        Observed[0].Should().NotBe(Observed[2], "and each run gets a fresh scope");
    }

    [Test]
    public void Refuses_to_register_two_pipelines_under_one_name()
    {
        //arrange
        // A name identifies a pipeline for resolution and keys its components, so it has to be
        // unique. Without this check the second registration silently replaced the first: the
        // factory listed one name, and the first pipeline's sink never received a row.
        var services = NewServices();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        //act
        var act = () => services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        //assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*")
            .WithMessage("*must be unique*");
    }

    [Test]
    public async Task Keeps_pipelines_registered_under_different_names_apart()
    {
        //arrange
        var services = NewServices();

        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        services.AddEtlPipeline(OtherPipelineName, builder => builder
            .From<OrderRow>(_ => new DownloadStage(NullLogger<DownloadStage>.Instance)
            {
                Rows = Orders.Take(1).ToArray(),
            })
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        //act
        var all = await provider.GetRequiredEtlPipeline(PipelineName).RunAsync(CancellationToken.None);
        var one = await provider.GetRequiredEtlPipeline(OtherPipelineName).RunAsync(CancellationToken.None);

        //assert
        provider.GetRequiredService<IPipelineFactory>().Names
            .Should().BeEquivalentTo(PipelineName, OtherPipelineName);
        all.Value.RowsWritten.Should().Be(3);
        one.Value.RowsWritten.Should().Be(1);
    }

    [Test]
    public void Says_so_plainly_when_no_pipelines_are_registered_at_all()
    {
        //arrange
        var provider = new ServiceCollection().BuildServiceProvider();

        //act
        var act = () => provider.GetRequiredEtlPipeline(PipelineName);

        //assert
        // Distinct from the wrong-name case below: forgetting AddEtlPipeline entirely is a different
        // mistake, and the container's stock "no service for type IPipelineFactory" names neither.
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No ETL pipelines are registered*")
            .WithMessage("*AddEtlPipeline*");
    }

    [Test]
    public void Lists_the_known_names_when_asked_for_one_that_does_not_exist()
    {
        //arrange
        var services = NewServices();
        services.AddEtlPipeline(PipelineName, builder => builder
            .From<DownloadStage, OrderRow>()
            .Through<TransformStage, OrderDto>()
            .To<UploadStage>());

        var provider = services.BuildServiceProvider();

        //act
        var act = () => provider.GetRequiredEtlPipeline(OtherPipelineName);

        //assert
        act.Should().Throw<InvalidOperationException>().WithMessage($"*{PipelineName}*");
    }

    [Test]
    public void Refuses_to_build_a_pipeline_with_no_stages()
    {
        //arrange
        var builder = EtlPipeline.CreateBuilder("empty");

        //act
        var act = builder.Build;

        //assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*no stages*");
    }

    /// <summary>A container with the logging the sample ports take in their constructors.</summary>
    private static ServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        return services;
    }

    /// <summary>A scoped dependency whose identity reveals which scope resolved it.</summary>
    private sealed class RunMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
}
