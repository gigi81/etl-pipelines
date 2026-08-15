using EtlPipelines.Abstractions.Configuration;

namespace EtlPipelines.Abstractions.Execution;

/// <summary>What one stage did during a run.</summary>
/// <param name="Name">The stage's name, for logs and metrics.</param>
/// <param name="RowsIn">Rows the stage consumed.</param>
/// <param name="RowsOut">
/// Rows the stage produced. Deliberately tracked separately from <paramref name="RowsIn"/>: a filter
/// or an aggregation makes the two diverge, and collapsing them into one "rows processed" number
/// would hide exactly the information you need when a load comes out short.
/// </param>
/// <param name="RowsFailed">Rows rejected under the configured <see cref="RowErrorAction"/>.</param>
/// <param name="Elapsed">Wall-clock time the stage was active.</param>
public sealed record StageResult(
    string Name,
    long RowsIn,
    long RowsOut,
    long RowsFailed,
    TimeSpan Elapsed)
{
    /// <summary>Rows per second through this stage, or <c>0</c> when it ran too briefly to measure.</summary>
    public double RowsPerSecond => Elapsed.TotalSeconds > 0 ? RowsIn / Elapsed.TotalSeconds : 0;
}
