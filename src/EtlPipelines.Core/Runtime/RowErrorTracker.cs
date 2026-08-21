
namespace EtlPipelines.Core.Runtime;

/// <summary>
/// Applies the configured <see cref="RowErrorAction"/> and keeps the running failure count.
/// </summary>
internal sealed class RowErrorTracker(PipelineOptions options)
{
    private long _failed;

    /// <summary>Rows rejected so far.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Whether rejected rows should be routed to a dead-letter sink.</summary>
    public bool DeadLetters => options.OnRowError == RowErrorAction.DeadLetter;

    /// <summary>
    /// Records rejected rows and reports whether the run must now stop.
    /// </summary>
    /// <returns>
    /// The error that should fail the run, or <see langword="null"/> when the pipeline may continue.
    /// </returns>
    public Error? Record(Error error, int rowCount = 1)
    {
        if (options.OnRowError == RowErrorAction.Fail)
        {
            return error;
        }

        var total = Interlocked.Add(ref _failed, rowCount);

        // MaxRowErrors of zero means unlimited: the tolerance is already stated by the action.
        if (options.MaxRowErrors > 0 && total > options.MaxRowErrors)
        {
            return Error.Failure(
                "pipeline.too_many_row_errors",
                $"Row error threshold exceeded: {total} rejected rows, limit {options.MaxRowErrors}. " +
                $"Last error: {error.Code} — {error.Description}");
        }

        return null;
    }
}
