namespace EtlPipelines.Core;

/// <summary>Sugar over <see cref="IPipelineBuilder"/> that needs nothing beyond its public members.</summary>
public static class PipelineBuilderExtensions
{
    /// <summary>Appends a coarse job stage only when <paramref name="condition"/> holds.</summary>
    /// <remarks>
    /// For a stage a pipeline needs only sometimes - creating tables it owns, say, but not when a
    /// caller handed it a connection to a database it does not - so the choice can stay in the fluent
    /// chain instead of breaking it into an <c>if</c> block sitting between two calls that belong
    /// together.
    /// </remarks>
    public static IPipelineBuilder AddConditionalStage<TStage>(
        this IPipelineBuilder builder,
        bool condition,
        string? name = null)
        where TStage : class, IPipelineStage
    {
        ArgumentNullException.ThrowIfNull(builder);

        return condition ? builder.AddStage<TStage>(name) : builder;
    }
}
