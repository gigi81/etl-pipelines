using EtlPipelines.Abstractions.Lifecycle;

namespace EtlPipelines.Abstractions.Execution;

/// <summary>
/// One unit of work in a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// This is the non-generic execution boundary. An entire typed dataflow — source, transforms, sink —
/// compiles down to a <i>single</i> stage, which is where the generic builder's type information is
/// erased and handed to the executor. Coarse job steps that move no rows through the framework
/// (downloading files, running a stored procedure, swapping a staging table into place) implement
/// this interface directly.
/// </para>
/// <para>
/// Both granularities therefore coexist without either being bent into the other's shape, and the
/// interface stays single-method so a stage can be expressed as a lambda. Setup and commit are
/// available by implementing <see cref="IAsyncInitializable"/> and <see cref="IAsyncCompletable"/>.
/// </para>
/// </remarks>
public interface IPipelineStage
{
    /// <summary>The stage's name, used for logging, tracing and metrics.</summary>
    string Name { get; }

    /// <summary>Runs the stage to completion.</summary>
    ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken);
}
