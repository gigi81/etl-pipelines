using System.Diagnostics;

namespace EtlPipelines.Runtime;

/// <summary>Adapts an inline delegate to <see cref="IPipelineStage"/>, for glue steps not worth a class.</summary>
internal sealed class DelegateStage(
    string name,
    Func<PipelineContext, CancellationToken, ValueTask<ErrorOr<Success>>> execute) : IPipelineStage
{
    public string Name { get; } = name;

    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var result = await execute(context, cancellationToken).ConfigureAwait(false);

        return result.IsError
            ? result.Errors
            : new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
