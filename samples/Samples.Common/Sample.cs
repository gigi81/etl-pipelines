using EtlPipelines.Abstractions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.Common;

/// <summary>
/// One sample: a pipeline, whatever it needs in place before it runs, and anything worth saying
/// about the result afterwards.
/// </summary>
/// <remarks>
/// A sample is not a program. It registers a pipeline into a container it does not own and is run by
/// <see cref="SampleHost"/>, which is what lets the same class be started from a command line and
/// driven from a test without either of them being a special case of the other.
/// </remarks>
public abstract class Sample
{
    /// <summary>The name the pipeline is registered under, and the one the host resolves to run it.</summary>
    public abstract string PipelineName { get; }

    /// <summary>One line saying what this sample shows. Logged before the run.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// Registers the pipeline, along with anything it resolves from the container — a dead-letter
    /// sink, a bulk loader, options.
    /// </summary>
    /// <remarks>Runs while the host is being built, so nothing here should touch the filesystem.</remarks>
    public abstract void Register(IServiceCollection services, SampleWorkspace workspace);

    /// <summary>
    /// Puts in place what the pipeline expects to find: the incoming file, the target table.
    /// </summary>
    /// <remarks>
    /// Runs after the host is built and before the pipeline does, which is the only ordering that
    /// works — a real job's input arrives from somewhere else entirely, so this stands in for it.
    /// </remarks>
    public virtual Task PrepareAsync(SampleWorkspace workspace, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Reports anything the row counts do not already say — rows set aside, rows that reached a
    /// table. Only worth overriding when there is something to add.
    /// </summary>
    public virtual Task ReportAsync(SampleOutcome outcome, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>What a sample run produced, and what it can still be asked.</summary>
/// <param name="Result">Row counts and per-stage timings from the run.</param>
/// <param name="Workspace">The directory the run wrote into.</param>
/// <param name="Services">
/// The container the pipeline ran in, so a sample — or a test — can reach whatever it registered.
/// </param>
/// <param name="Logger">Where a sample should write anything it wants a reader to see.</param>
public sealed record SampleOutcome(
    PipelineResult Result,
    SampleWorkspace Workspace,
    IServiceProvider Services,
    ILogger Logger);
