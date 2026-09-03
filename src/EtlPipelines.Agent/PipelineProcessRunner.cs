using CliWrap;
using CliWrap.Buffered;

namespace EtlPipelines.Agent;

/// <summary>
/// Runs an installed pipeline package's shim - <c>list</c> to discover its pipeline names,
/// <c>run &lt;name&gt; --session-id ... --server-url ...</c> to execute one. Phase 5 only ever
/// calls <see cref="ListPipelineNamesAsync"/> (right after <see cref="PackageInstaller"/>
/// installs a package, to report its pipeline names back); <see cref="RunAsync"/> exists now
/// because SERVER.md names both as one type's job, but nothing dispatches an
/// <c>ExecutePipeline</c> work item to call it until Phase 6.
/// </summary>
public static class PipelineProcessRunner
{
    /// <summary>Runs <c>&lt;shimPath&gt; list</c> and returns the pipeline names it printed, one per line.</summary>
    public static async Task<IReadOnlyList<string>> ListPipelineNamesAsync(string shimPath, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(shimPath, ["list"], cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{shimPath} list' failed: {Tail(result)}");
        }

        // One pipeline name per line - ListPipelinesHandler's own contract (Writer.WriteLine, not
        // the logger), proven to survive packaging by Phase 1's own verification.
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Runs <c>&lt;shimPath&gt; run &lt;pipelineName&gt; --session-id &lt;sessionId&gt;
    /// --server-url &lt;serverUrl&gt;</c> and returns its exit code. Standard output/error are
    /// piped to this process's own, unbuffered - unlike <see cref="ListPipelineNamesAsync"/>,
    /// nothing here needs to parse them (the launched process reports its own results back to
    /// <c>PipelineExecutionService</c> over gRPC, not through captured console output), but a
    /// pipeline that fails - or crashes outright, never getting the chance to report anything
    /// itself - should still leave its own diagnostics somewhere an operator watching this
    /// agent's own console can actually see, rather than silently discarded (CliWrap's own
    /// default target for an unconfigured pipe).
    /// </summary>
    public static async Task<int> RunAsync(string shimPath, string pipelineName, string sessionId, string serverUrl, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Cli.Wrap(shimPath)
                .WithArguments(["run", pipelineName, "--session-id", sessionId, "--server-url", serverUrl])
                .WithStandardOutputPipe(PipeTarget.ToStream(Console.OpenStandardOutput()))
                .WithStandardErrorPipe(PipeTarget.ToStream(Console.OpenStandardError()))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            return result.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"'{shimPath}' could not be started.", exception);
        }
    }

    private static async Task<BufferedCommandResult> ExecuteAsync(string shimPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await Cli.Wrap(shimPath)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"'{shimPath}' could not be started.", exception);
        }
    }

    private static string Tail(BufferedCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() : "(no output)";
    }
}
