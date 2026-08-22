using EtlPipelines.Abstractions.Execution;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Hosting;

/// <summary>
/// Runs a named pipeline and reports what it did.
/// </summary>
/// <remarks>
/// The piece every command-line front end needs and none of them should write twice: find the
/// pipeline, run it, turn the result into something a person can read and a shell can branch on.
/// Injected rather than constructed, so a command of your own can use it exactly as the built-in
/// <c>run</c> verb does.
/// </remarks>
public class PipelineRunner
{
    private readonly IEnumerable<IPipeline> _pipelines;
    private readonly ILogger<PipelineRunner> _logger;

    /// <summary>Runs any of the pipelines registered in the container.</summary>
    public PipelineRunner(IEnumerable<IPipeline> pipelines, ILogger<PipelineRunner> logger)
    {
        _pipelines = pipelines;
        _logger = logger;
    }

    /// <summary>Every pipeline registered in the container, in registration order.</summary>
    public IEnumerable<IPipeline> Pipelines => _pipelines;

    /// <summary>Finds a pipeline by name, or <see langword="null"/> when none is registered under it.</summary>
    public IPipeline? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _pipelines.FirstOrDefault(
            pipeline => string.Equals(pipeline.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Runs the pipeline registered under <paramref name="name"/>.</summary>
    /// <returns>A process exit code: <c>0</c> when the run succeeded, <c>1</c> when it did not.</returns>
    public async Task<int> RunAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var pipeline = Find(name);

        if (pipeline is null)
        {
            _logger.LogError(
                "No pipeline named '{Pipeline}' is registered. Registered pipelines: {Registered}",
                name,
                Names());

            return 1;
        }

        return await RunAsync(pipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a pipeline that has already been resolved.</summary>
    /// <returns>A process exit code: <c>0</c> when the run succeeded, <c>1</c> when it did not.</returns>
    public async Task<int> RunAsync(IPipeline pipeline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var run = await pipeline.RunAsync(cancellationToken).ConfigureAwait(false);

        if (run.IsError)
        {
            foreach (var error in run.Errors)
            {
                _logger.LogError("{Pipeline} failed - {Code}: {Description}", pipeline.Name, error.Code, error.Description);
            }

            return 1;
        }

        Report(run.Value);
        return 0;
    }

    private string Names()
    {
        var names = _pipelines.Select(pipeline => pipeline.Name).ToArray();
        return names.Length == 0 ? "(none)" : string.Join(", ", names);
    }

    private void Report(PipelineResult result)
    {
        _logger.LogInformation(
            "{Pipeline}: read {RowsRead}, wrote {RowsWritten}, failed {RowsFailed}, in {Elapsed:F0} ms",
            result.Name,
            result.RowsRead,
            result.RowsWritten,
            result.RowsFailed,
            result.Elapsed.TotalMilliseconds);

        foreach (var stage in result.Stages)
        {
            _logger.LogInformation(
                "  stage {Stage}: {RowsIn} in, {RowsOut} out, {RowsPerSecond:F0} rows/s",
                stage.Name,
                stage.RowsIn,
                stage.RowsOut,
                stage.RowsPerSecond);
        }
    }
}
