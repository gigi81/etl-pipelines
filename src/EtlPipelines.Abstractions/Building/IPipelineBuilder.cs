using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Abstractions.Ports;

namespace EtlPipelines.Abstractions.Building;

/// <summary>
/// Composes a pipeline from stages and typed dataflows.
/// </summary>
/// <remarks>
/// The builder keeps its own ordered list of stage descriptors rather than relying on service
/// registration order. Registering a stage type into a container records no position, which is why
/// composition and dependency registration are kept as separate concerns here.
/// </remarks>
public interface IPipelineBuilder
{
    /// <summary>Adjusts batch size, channel capacity and row-error policy for this pipeline.</summary>
    IPipelineBuilder WithOptions(Action<PipelineOptions> configure);

    /// <summary>Appends a coarse job stage, resolved from the service provider at run time.</summary>
    IPipelineBuilder AddStage<TStage>(string? name = null) where TStage : class, IPipelineStage;

    /// <summary>Appends an already-constructed stage.</summary>
    IPipelineBuilder AddStage(IPipelineStage stage);

    /// <summary>Appends a stage defined inline. Useful for glue steps not worth a class.</summary>
    IPipelineBuilder AddStage(
        string name,
        Func<PipelineContext, CancellationToken, ValueTask<ErrorOr<Success>>> execute);

    /// <summary>
    /// Begins a typed dataflow whose source is resolved from the service provider.
    /// </summary>
    /// <remarks>
    /// Only the row type is named. Spelling the source's concrete type as well would mean writing
    /// <c>From&lt;CsvSource, Order&gt;()</c> at every call site, because C# has no partial generic
    /// inference — naming just the row type keeps the meaningful half and lets the terminating
    /// <see cref="IDataflowBuilder{TRow}.To()"/> infer completely.
    /// </remarks>
    IDataflowBuilder<TRow> From<TRow>();

    /// <summary>Begins a typed dataflow from a keyed service, for when two sources share a row type.</summary>
    IDataflowBuilder<TRow> From<TRow>(object serviceKey);

    /// <summary>Begins a typed dataflow from an existing source instance.</summary>
    IDataflowBuilder<TRow> From<TRow>(IDataSource<TRow> source);

    /// <summary>Begins a typed dataflow from a source factory.</summary>
    IDataflowBuilder<TRow> From<TRow>(Func<IServiceProvider, IDataSource<TRow>> factory);

    /// <summary>Produces the runnable pipeline.</summary>
    IPipeline Build();
}
