namespace EtlPipelines.Sql.Scripts;

/// <summary>
/// Where a script is read from.
/// </summary>
/// <remarks>
/// A file on disk and a resource compiled into an assembly are the two that ship, and they differ
/// only in how the text is reached — so the stage that runs a script takes one of these rather than
/// either of them, and gains nothing to know about deployment layout.
/// </remarks>
public interface ISqlScriptSource
{
    /// <summary>What the script is called, in the run's report and in errors.</summary>
    string Name { get; }

    /// <summary>
    /// Opens the script. The caller disposes the reader.
    /// </summary>
    /// <returns>
    /// An error when the script is not there, rather than an exception: a missing script is an
    /// ordinary way for a run to fail and reads better reported alongside every other failure.
    /// </returns>
    ValueTask<ErrorOr<TextReader>> OpenAsync(CancellationToken cancellationToken);
}
