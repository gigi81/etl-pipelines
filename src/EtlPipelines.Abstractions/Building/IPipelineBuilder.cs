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
    /// Begins a typed dataflow from a source the pipeline constructs and owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the usual form. Naming the concrete type here <i>is</i> the registration — there is no
    /// separate <c>services.AddSingleton&lt;IDataSource&lt;Order&gt;, CsvSource&gt;()</c> to write and
    /// keep in step. Constructor dependencies are still injected from the container.
    /// </para>
    /// <para>
    /// The source is built fresh for each run, which is what a stateful port needs: a source tracks
    /// its read position and an aggregate accumulates state, so sharing one across runs would carry
    /// the previous run's leftovers into the next. Use <see cref="From{TRow}()"/> instead when the
    /// container should own the lifetime.
    /// </para>
    /// <para>
    /// Both type arguments are required because C# has no partial generic inference: <c>TRow</c>
    /// cannot be deduced from <c>TSource</c>. The terminating <see cref="IDataflowBuilder{TRow}.To{TSink}()"/>
    /// needs only one, since the row type is already known by then.
    /// </para>
    /// </remarks>
    IDataflowBuilder<TRow> From<TSource, TRow>() where TSource : class, IDataSource<TRow>;

    /// <summary>
    /// Begins a typed dataflow whose source is resolved from the service provider, for when the
    /// container already owns the port and its lifetime.
    /// </summary>
    IDataflowBuilder<TRow> From<TRow>();

    /// <summary>Begins a typed dataflow from a keyed service, for when two sources share a row type.</summary>
    IDataflowBuilder<TRow> From<TRow>(object serviceKey);

    /// <summary>Begins a typed dataflow from an existing source instance.</summary>
    IDataflowBuilder<TRow> From<TRow>(IDataSource<TRow> source);

    /// <summary>Begins a typed dataflow from a source factory.</summary>
    IDataflowBuilder<TRow> From<TRow>(Func<IServiceProvider, IDataSource<TRow>> factory);

    /// <summary>
    /// Runs several independent stage chains concurrently, waiting for all of them before continuing.
    /// </summary>
    /// <param name="branches">
    /// Two or more branch definitions. Each is composed exactly like the main chain — coarse stages,
    /// dataflows, even a nested <see cref="Parallel"/> — and runs its own stages one after another;
    /// it is the branches that overlap with each other, not the stages inside one.
    /// </param>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="IDataflowBuilder{TRow}.Branch"/>, which fans <i>one already-flowing stream</i>
    /// out to several destinations, this fans out at the coarse-stage level: each branch reads and
    /// writes something of its own — its own file, its own table — with nothing shared between them
    /// until they all finish. Five independent extract-and-load chains against five different tables
    /// are the motivating case; a single dataflow has no use for this.
    /// </para>
    /// <para>
    /// <b>One branch failing does not cancel the others.</b> Each already-running branch keeps going to
    /// completion — cutting one off mid-write would trade a slow run for a half-written table, which is
    /// worse. Once every branch has finished, the block fails if any of them did, reporting the first
    /// error; whatever rows the other branches moved are still counted, the same way a failed dataflow
    /// stage still reports what it moved before it died.
    /// </para>
    /// <para>
    /// The whole block is still a single entry in <c>PipelineResult.Stages</c> — row counts summed
    /// across branches, elapsed time the wall clock of the block, not the sum of its branches — because
    /// a caller reading the run's report cares what the block as a whole moved. Each branch's own
    /// stages are still traced individually, so per-branch detail is not lost, only rolled up here.
    /// </para>
    /// </remarks>
    IPipelineBuilder Parallel(params Action<IPipelineBuilder>[] branches);

    /// <summary>Produces the runnable pipeline.</summary>
    IPipeline Build();
}
