namespace EtlPipelines.Extensions.Cli.Configuration;

/// <summary>Settings for running one external command as a pipeline stage.</summary>
public sealed class CliCommandOptions
{
    /// <summary>
    /// What the stage is called in the run's report, traces and metrics. Defaults to a name derived
    /// from the command, or to the script's file name for a PowerShell script.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>The directory the process starts in. Left <see langword="null"/>, it inherits this process's.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Environment variables added to the process, on top of what it inherits.</summary>
    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; set; }

    /// <summary>How long the process may run before it is cancelled. Left <see langword="null"/>, there is no limit here.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Exit codes treated as success. Defaults to <c>{ 0 }</c> - an escape hatch for tools such as
    /// installers that use small nonzero codes for a non-error outcome.
    /// </summary>
    public IReadOnlyCollection<int>? SuccessExitCodes { get; set; }

    /// <summary>
    /// Adjusts the command before it runs - credentials, resource limits, a stdin pipe. A transform
    /// rather than a mutator: <see cref="Command"/> is immutable, so every <c>With*</c> call on it
    /// returns a copy, and this must return the copy the stage should actually run.
    /// </summary>
    public Func<Command, Command>? Configure { get; set; }
}
