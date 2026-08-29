namespace EtlPipelines.Cli.Stages;

/// <summary>
/// Wraps a stage so it fails clearly on any platform but Windows, instead of the wrapped stage
/// attempting - and failing less informatively - to start a process that was never going to exist
/// there.
/// </summary>
internal sealed class WindowsOnlyStage : IPipelineStage
{
    private readonly IPipelineStage _inner;
    private readonly string _errorCode;

    public WindowsOnlyStage(IPipelineStage inner, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        _inner = inner;
        _errorCode = errorCode;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? _inner.ExecuteAsync(context, cancellationToken)
            : ValueTask.FromResult<ErrorOr<StageResult>>(Error.Failure(
                _errorCode,
                $"'{Name}' requires Windows PowerShell (powershell.exe), which is not available on this platform."));
}
