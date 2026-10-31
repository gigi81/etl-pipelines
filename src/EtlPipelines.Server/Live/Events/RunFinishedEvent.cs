namespace EtlPipelines.Server.Live.Events;

/// <summary>A run ended - it succeeded, failed or was lost - at <see cref="LiveEvent.AtUtc"/>.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Status"><c>Succeeded</c>, <c>Failed</c> or <c>AgentLost</c>.</param>
/// <param name="ExitCode">The process's exit code; null leaves the run's as it is.</param>
/// <param name="RowsRead">Rows read; null leaves the run's as it is.</param>
/// <param name="RowsWritten">Rows written; null leaves the run's as it is.</param>
/// <param name="RowsFailed">Rows that failed; null leaves the run's as it is.</param>
/// <param name="AtUtc">When it ended.</param>
public sealed record RunFinishedEvent(
    Guid RunId,
    string Status,
    int? ExitCode,
    long? RowsRead,
    long? RowsWritten,
    long? RowsFailed,
    DateTime AtUtc) : LiveEvent(AtUtc);
