namespace EtlPipelines.Extensions.Cli.Stages;

/// <summary>
/// Wraps a stage that needs a file to exist first, checking it right before the stage runs rather
/// than when the pipeline was composed.
/// </summary>
/// <remarks>
/// Checked here, not at build time: the file - a PowerShell script, say - perfectly well may not
/// exist until an earlier stage of this same run has fetched it, the same reasoning
/// <c>EtlPipelines.Extensions.Sql.Scripts.FileSqlScriptSource</c> gives for refreshing rather than trusting the
/// <see cref="IFileInfo"/> it was handed.
/// </remarks>
internal sealed class RequireFileStage : IPipelineStage
{
    private readonly IFileInfo _file;
    private readonly IPipelineStage _inner;
    private readonly string _errorCode;

    public RequireFileStage(IFileInfo file, IPipelineStage inner, string errorCode)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);

        _file = file;
        _inner = inner;
        _errorCode = errorCode;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        _file.Refresh();

        if (!_file.Exists)
        {
            return ValueTask.FromResult<ErrorOr<StageResult>>(
                Error.Failure(_errorCode, $"The script '{_file.FullName}' does not exist."));
        }

        return _inner.ExecuteAsync(context, cancellationToken);
    }
}
