using System.Diagnostics;

namespace EtlPipelines.Core.Runtime;

/// <summary>
/// A whole source → transforms → sink dataflow, presented to the executor as one stage.
/// </summary>
/// <remarks>
/// This is where the typed builder's generics are erased. Inside, every node runs concurrently and
/// exchanges batches over bounded channels, so extract, transform and load overlap: batch N+1 is
/// being read while batch N is still being written. Sequencing them instead would leave every port
/// idle waiting for the others, which is the single largest throughput cost an ETL engine can pay.
/// </remarks>
internal sealed class DataflowStage(string name, IReadOnlyList<DataflowNode> template) : IPipelineStage
{
    public string Name { get; } = name;

    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        // The builder produced one set of nodes; this run gets its own copies. Reusing them would
        // make row counts accumulate across runs and let concurrent runs corrupt each other's state.
        var nodes = new DataflowNode[template.Count];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = template[i].CreateInstance();
        }

        var started = Stopwatch.StartNew();
        var tracker = new RowErrorTracker(context.Options);

        using var run = new DataflowRunContext(context, tracker, cancellationToken);

        try
        {
            object? channel = null;
            foreach (var node in nodes)
            {
                channel = node.Start(channel, run);
            }

            await Task.WhenAll(nodes.Select(n => n.Completion)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Fail(Error.Failure($"stage.{Name}.faulted", $"{ex.GetType().Name}: {ex.Message}"));
        }

        var elapsed = started.Elapsed;
        var error = run.FirstError;

        // Distinguish a caller cancellation from a clean finish: nodes swallow cancellation so the
        // stage can report it once, coherently, instead of as a torn set of partial failures.
        if (error is null && cancellationToken.IsCancellationRequested)
        {
            error = Error.Failure($"stage.{Name}.cancelled", $"Stage '{Name}' was cancelled.");
        }

        var first = nodes[0];
        var last = nodes[^1];
        var result = new StageResult(Name, first.RowsIn, last.RowsOut, tracker.Failed, elapsed);

        // The run loop in EtlPipeline records every stage now, on both paths. On this one ErrorOr
        // carries either the value or the errors and never both, so the counts this stage did move
        // ride along on the error itself, where the loop can find them again.
        return error is { } failure ? failure.WithStageResult(result) : result;
    }
}
