using System.ComponentModel;
using System.Diagnostics;
using CliWrap.Buffered;
using CliWrap.Exceptions;

namespace EtlPipelines.Extensions.Cli.Stages;

/// <summary>
/// Runs one external command as a stage of a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The general-purpose counterpart of <c>EtlPipelines.Extensions.Sql.Stages.SqlCommandStage</c>: a stage that
/// moves no rows through the framework but hands work to a process outside it. Row counts are
/// reported as zero for the same reason - the command's own effects were never a batch here.
/// </para>
/// <para>
/// Run with CliWrap's own exit-code validation switched off; this stage inspects
/// <see cref="CliWrap.CommandResult.ExitCode"/> itself against
/// <see cref="Configuration.CliCommandOptions.SuccessExitCodes"/>; a non-zero code that isn't in that set becomes
/// an <see cref="ErrorOr{TValue}"/> failure carrying the process's own stderr (or stdout, if it wrote
/// nothing to stderr), rather than an exception.
/// </para>
/// </remarks>
public sealed class CliCommandStage : IPipelineStage
{
    private static readonly IReadOnlyCollection<int> DefaultSuccessExitCodes = [0];

    private readonly string _targetFilePath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly Configuration.CliCommandOptions _options;

    /// <summary>Runs <paramref name="targetFilePath"/> with <paramref name="arguments"/>.</summary>
    /// <param name="targetFilePath">The executable to run.</param>
    /// <param name="arguments">The arguments to pass it, in order.</param>
    /// <param name="options">Working directory, environment, timeout, and what the stage is called.</param>
    public CliCommandStage(
        string targetFilePath,
        IEnumerable<string>? arguments = null,
        Configuration.CliCommandOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);

        _targetFilePath = targetFilePath;
        _arguments = arguments as IReadOnlyList<string> ?? [.. arguments ?? []];
        _options = options ?? new Configuration.CliCommandOptions();

        Name = _options.Name ?? CommandName.Truncate(string.Join(' ', new[] { targetFilePath }.Concat(_arguments)));
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.StartNew();

        using var linked = _options.Timeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        linked?.CancelAfter(_options.Timeout!.Value);
        var token = linked?.Token ?? cancellationToken;

        // Fully qualified: CliWrap.Cli would otherwise resolve against this project's own
        // EtlPipelines.Extensions.Cli namespace, which encloses this file, rather than the CliWrap package.
        var command = CliWrap.Cli.Wrap(_targetFilePath)
            .WithArguments(_arguments)
            .WithValidation(CommandResultValidation.None);

        if (_options.WorkingDirectory is { } workingDirectory)
        {
            command = command.WithWorkingDirectory(workingDirectory);
        }

        if (_options.EnvironmentVariables is { } environmentVariables)
        {
            command = command.WithEnvironmentVariables(environmentVariables);
        }

        if (_options.Configure is { } configure)
        {
            command = configure(command);
        }

        BufferedCommandResult result;

        try
        {
            result = await command.ExecuteBufferedAsync(token).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            return Error.Failure(
                $"cli.command.{Name}.not_found",
                $"'{_targetFilePath}' could not be started: {exception.Message}");
        }
        catch (CommandExecutionException exception)
        {
            // Only reachable if Configure re-enabled CliWrap's own exit-code validation - reported
            // the same way a failure this stage caught itself would be.
            return Error.Failure($"cli.command.{Name}.exit_code", $"'{Name}' exited with code {exception.ExitCode}. {exception.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error.Failure(
                $"cli.command.{Name}.timed_out",
                $"'{Name}' did not complete within {_options.Timeout}.");
        }

        var successExitCodes = _options.SuccessExitCodes ?? DefaultSuccessExitCodes;

        if (!successExitCodes.Contains(result.ExitCode))
        {
            return Error.Failure(
                $"cli.command.{Name}.exit_code",
                $"'{Name}' exited with code {result.ExitCode}. {Tail(result)}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    private static string Tail(BufferedCommandResult result) =>
        !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError.Trim()
        : !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim()
        : "(no output)";
}
