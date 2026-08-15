using EtlPipelines.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines;

/// <summary>
/// Builds a pipeline from an ordered list of stages, registering each component into the container
/// as it goes.
/// </summary>
/// <remarks>
/// <para>
/// Order is tracked here rather than inferred from container registration, because registering a
/// type records no position. Everything else about a component's lifetime, though, is the
/// container's job: each one is registered <b>scoped</b> and <b>keyed to this pipeline</b>, then
/// resolved from the scope a run creates. Scoped gives per-run instances, which is what a stateful
/// component needs — a source tracks read position, an aggregate accumulates. Keyed keeps two
/// pipelines that use the same component type from overwriting each other's registration.
/// </para>
/// <para>
/// Because registration happens during composition, the builder needs the
/// <see cref="IServiceCollection"/> rather than a built provider.
/// </para>
/// </remarks>
public sealed class EtlPipelineBuilder : IPipelineBuilder
{
    private readonly List<Func<IServiceProvider, IPipelineStage>> _stages = [];
    private readonly IServiceCollection _services;
    private readonly PipelineOptions _options = new();
    private readonly string _name;
    private int _ordinal;

    /// <summary>Creates a builder that registers its components into <paramref name="services"/>.</summary>
    public EtlPipelineBuilder(string name, IServiceCollection services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(services);

        _name = name;
        _services = services;
    }

    /// <summary>The collection components are registered into.</summary>
    internal IServiceCollection Services => _services;

    /// <summary>Issues the next unique key for a component of this pipeline.</summary>
    internal EtlComponentKey NextKey(string role) => new(_name, _ordinal++, role);

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
        var key = NextKey("stage");
        _services.AddKeyedScoped<TStage>(key);

        _stages.Add(services =>
        {
            var stage = services.GetRequiredKeyedService<TStage>(key);
            return name is null ? stage : new RenamedStage(name, stage);
        });

        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder AddStage(IPipelineStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);

        var key = NextKey("stage");
        _services.AddKeyedScoped<IPipelineStage>(key, (_, _) => stage);
        _stages.Add(services => services.GetRequiredKeyedService<IPipelineStage>(key));

        return this;
    }

    /// <inheritdoc />
    public IPipelineBuilder AddStage(
        string name,
        Func<PipelineContext, CancellationToken, ValueTask<ErrorOr<Success>>> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execute);

        var key = NextKey("stage");
        _services.AddKeyedScoped<IPipelineStage>(key, (_, _) => new DelegateStage(name, execute));
        _stages.Add(services => services.GetRequiredKeyedService<IPipelineStage>(key));

        return this;
    }

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TSource, TRow>() where TSource : class, IDataSource<TRow>
    {
        var key = NextKey("source");
        _services.AddKeyedScoped<IDataSource<TRow>, TSource>(key);
        return StartDataflow(typeof(TSource).Name, Keyed<IDataSource<TRow>>(key));
    }

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>() =>
        StartDataflow(typeof(TRow).Name, Required<IDataSource<TRow>>);

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>(object serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceKey);
        return StartDataflow(typeof(TRow).Name, Keyed<IDataSource<TRow>>(serviceKey));
    }

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>(IDataSource<TRow> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var key = NextKey("source");
        _services.AddKeyedScoped<IDataSource<TRow>>(key, (_, _) => source);
        return StartDataflow(source.GetType().Name, Keyed<IDataSource<TRow>>(key));
    }

    /// <inheritdoc />
    public IDataflowBuilder<TRow> From<TRow>(Func<IServiceProvider, IDataSource<TRow>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var key = NextKey("source");
        _services.AddKeyedScoped<IDataSource<TRow>>(key, (services, _) => factory(services));
        return StartDataflow(typeof(TRow).Name, Keyed<IDataSource<TRow>>(key));
    }

    /// <inheritdoc />
    public IPipeline Build() => new EtlPipeline(CreateBlueprint(), _services.BuildServiceProvider());

    /// <summary>Captures the composed pipeline so a provider built later can run it.</summary>
    internal PipelineBlueprint CreateBlueprint()
    {
        _options.Validate();

        if (_stages.Count == 0)
        {
            throw new InvalidOperationException(
                $"Pipeline '{_name}' has no stages. Add one with AddStage, or a dataflow with From(...).To(...).");
        }

        return new PipelineBlueprint(_name, _stages.ToArray(), _options);
    }

    internal void AddDataflow(IReadOnlyList<DataflowNode> nodes)
    {
        var name = $"{nodes[0].Name} -> {nodes[^1].Name}";
        _stages.Add(_ => new DataflowStage(name, nodes));
    }

    /// <summary>Resolves a component the container owns under a key.</summary>
    internal static Func<IServiceProvider, T> Keyed<T>(object key) where T : class =>
        services => services.GetRequiredKeyedService<T>(key);

    /// <summary>Resolves a component the caller registered themselves, unkeyed.</summary>
    internal static T Required<T>(IServiceProvider services) where T : class =>
        services.GetService(typeof(T)) as T
        ?? throw new InvalidOperationException(
            $"No {typeof(T).Name} is registered. Register one, or name the concrete type in the " +
            "pipeline definition so it is registered for you.");

    private DataflowBuilder<TRow> StartDataflow<TRow>(string name, Func<IServiceProvider, IDataSource<TRow>> factory) =>
        new(this, [new SourceNode<TRow>(name, factory)]);
}

/// <summary>A composed pipeline, awaiting a service provider to run against.</summary>
internal sealed record PipelineBlueprint(
    string Name,
    IReadOnlyList<Func<IServiceProvider, IPipelineStage>> Stages,
    PipelineOptions Options);

/// <summary>Gives a stage a caller-supplied name without the stage having to know about it.</summary>
internal sealed class RenamedStage(string name, IPipelineStage inner) : IPipelineStage
{
    public string Name { get; } = name;

    public ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken) =>
        inner.ExecuteAsync(context, cancellationToken);
}
