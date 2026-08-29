using EtlPipelines.Extensions.Cli.Stages;
using EtlPipelines.Extensions.Cli.Configuration;

// ReSharper disable once CheckNamespace
namespace EtlPipelines;

/// <summary>Runs external programs - including PowerShell scripts - as pipeline stages.</summary>
public static class ExtensionsCli
{
    private const string Pwsh = "pwsh";
    private const string WindowsPowerShell = "powershell.exe";

    /// <summary>Adds a stage that runs an external command.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="targetFilePath">The executable to run.</param>
    /// <param name="arguments">The arguments to pass it, in order.</param>
    /// <param name="configure">Working directory, environment, timeout, and what the stage is called.</param>
    /// <remarks>
    /// Runs whatever is on the machine already - a script, a compiled tool, an installer. Nothing
    /// here knows what the program is; see <see cref="RunPowerShellScript"/> and
    /// <see cref="RunWindowsPowerShellScript"/> for the two shapes this library gives PowerShell in
    /// particular.
    /// </remarks>
    public static IPipelineBuilder RunCommand(
        this IPipelineBuilder builder,
        string targetFilePath,
        IEnumerable<string>? arguments = null,
        Action<CliCommandOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);

        var options = new CliCommandOptions();
        configure?.Invoke(options);

        return builder.AddStage(new CliCommandStage(targetFilePath, arguments, options));
    }

    /// <summary>Adds a stage that runs a PowerShell script with <c>pwsh</c>.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="script">The script to run.</param>
    /// <param name="arguments">
    /// Arguments passed after the script, binding positionally to its own <c>param()</c> block (or
    /// to <c>$args</c> when it has none) - the reason this runs the script with <c>-File</c> rather
    /// than piping it in as text.
    /// </param>
    /// <param name="configure">Working directory, environment, timeout, and what the stage is called.</param>
    /// <remarks>
    /// Cross-platform: <c>pwsh</c> (PowerShell 7+) ships for Windows, Linux and macOS alike, and is
    /// assumed to already be on the machine - nothing here installs or detects it, so a missing
    /// <c>pwsh</c> surfaces as this stage's own "executable not found" failure at run time. Use
    /// <see cref="RunWindowsPowerShellScript"/> for the legacy, Windows-only <c>powershell.exe</c>.
    /// </remarks>
    public static IPipelineBuilder RunPowerShellScript(
        this IPipelineBuilder builder,
        IFileInfo script,
        IEnumerable<string>? arguments = null,
        Action<CliCommandOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(script);

        var options = new CliCommandOptions();
        configure?.Invoke(options);
        options.Name ??= script.Name;

        var stage = new CliCommandStage(Pwsh, PowerShellArguments(script, arguments), options);

        return builder.AddStage(new RequireFileStage(script, stage, $"cli.powershell.{stage.Name}.script_missing"));
    }

    /// <summary>Adds a stage that runs a PowerShell script with the legacy, Windows-only <c>powershell.exe</c>.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="script">The script to run.</param>
    /// <param name="arguments">Arguments passed after the script, binding positionally to its own <c>param()</c> block.</param>
    /// <param name="configure">Working directory, environment, timeout, and what the stage is called.</param>
    /// <remarks>
    /// Fails clearly, at run time, when the pipeline is not actually running on Windows, rather than
    /// letting the process start fail less informatively. Prefer <see cref="RunPowerShellScript"/>
    /// unless a script genuinely depends on Windows PowerShell 5.1's own behaviour.
    /// </remarks>
    public static IPipelineBuilder RunWindowsPowerShellScript(
        this IPipelineBuilder builder,
        IFileInfo script,
        IEnumerable<string>? arguments = null,
        Action<CliCommandOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(script);

        var options = new CliCommandOptions();
        configure?.Invoke(options);
        options.Name ??= script.Name;

        var stage = new CliCommandStage(WindowsPowerShell, PowerShellArguments(script, arguments), options);
        var requiresFile = new RequireFileStage(script, stage, $"cli.powershell.{stage.Name}.script_missing");

        return builder.AddStage(new WindowsOnlyStage(requiresFile, $"cli.powershell.{stage.Name}.unsupported_platform"));
    }

    private static IEnumerable<string> PowerShellArguments(IFileInfo script, IEnumerable<string>? arguments) =>
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script.FullName, .. arguments ?? []];
}
