using EtlPipelines.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EtlPipelines;

/// <summary>
/// Builds a pipeline from an ordered list of stage descriptors.
/// </summary>
/// <remarks>
/// Order is tracked here rather than inferred from container registration, because registering a
/// type records no position — which is why composition and dependency registration are separate
/// concerns in this design.
/// </remarks>
public sealed class EtlPipelineBuilder : IPipelineBuilder
{
    private readonly List<Func<IServiceProvider, IPipelineStage>> _stages = [];
    private readonly IServiceCollection _services;
    private readonly IServiceProvider? _provider;
    private readonly PipelineOptions _options = new();
    private readonly string _name;

    /// <summary>Creates a builder that owns its own container.</summary>
    public EtlPipelineBuilder(string name, IServiceCollection services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(services);

        _name = name;
        _services = services;
    }

    /// <summary>Creates a builder that resolves ports from an existing container.</summary>
    public EtlPipelineBuilder(string name, IServiceProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(provider);

        _name = name;
        _services = new ServiceCollection();
        _provider = provider;
    }

    /// <inheritdoc />
    public IPipelineBuilder WithOptions(Action<PipelineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        _options.Validate();
        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder AddStage<TStage>(string? name = null) where TStage : class, IPipelineStage
    {
        _services.TryAddTransient<TStage>();

        _stages.Add(services =>
        {
            // Resolve when registered so constructor injection and scoping apply; otherwise build it
            // directly, which keeps a stage usable without having to register it first.
            var stage = services.GetService(typeof(TStage)) as IPipelineStage
                ?? ActivatorUtilities.CreateInstance<TStage>(services);

            return name is null ? stage : new RenamedStage(name, stage);
        });

        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder AddStage(IPipelineStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _stages.Add(_ => stage);
        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder AddStage(
        string name,
        Func<PipelineContext, CancellationToken, ValueTask<ErrorOr<Success>>> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execute);

        _stages.Add(_ => new DelegateStage(name, execute));
        return this;
    }

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TSource, TRow>() where TSource : class, IDataSource<TRow> =>
        // Constructed per run rather than registered as a service: a source carries read position,
        // so one shared instance would resume mid-stream on the second run.
        From(services => ActivatorUtilities.CreateInstance<TSource>(services));

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>() =>
        From(Required<IDataSource<TRow>>);

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>(object serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        return From<TRow>(services => RequiredKeyed<IDataSource<TRow>>(services, serviceKey));
    }

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>(IDataSource<TRow> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return From<TRow>(_ => source);
    }

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>(Func<IServiceProvider, IDataSource<TRow>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new DataflowBuilder<TRow>(this, [new SourceNode<TRow>(typeof(TRow).Name, factory)]);
    }

    /// <inheritdoc />
    public IPipeline Build()
    {
        _options.Validate();

        if (_stages.Count == 0)
        {
            throw new InvalidOperationException(
                $"Pipeline '{_name}' has no stages. Add one with AddStage, or a dataflow with From(...).To(...).");
        }

        var provider = _provider ?? _services.BuildServiceProvider();
        return new EtlPipeline(_name, _stages, provider, _options);
    }

    internal void AddDataflow(IReadOnlyList<DataflowNode> nodes)
    {
        var name = $"{nodes[0].Name} -> {nodes[^1].Name}";
        _stages.Add(_ => new DataflowStage(name, nodes));
    }

    internal static T Required<T>(IServiceProvider services) where T : class =>
        services.GetService(typeof(T)) as T
        ?? throw new InvalidOperationException(
            $"No {typeof(T).Name} is registered. Register one, or pass the instance directly to From/Through/To.");

    private static T RequiredKeyed<T>(IServiceProvider services, object key) where T : class =>
        services is IKeyedServiceProvider keyed && keyed.GetKeyedService(typeof(T), key) is T resolved
            ? resolved
            : throw new InvalidOperationException(
                $"No {typeof(T).Name} is registered with key '{key}'.");
}

/// <summary>Gives a stage a caller-supplied name without the stage having to know about it.</summary>
internal sealed class RenamedStage(string name, IPipelineStage inner) : IPipelineStage
{
    public string Name { get; } = name;

    public ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken) =>
        inner.ExecuteAsync(context, cancellationToken);
}
