namespace EtlPipelines.Files.Configuration;

/// <summary>
/// What a multi-file stage does when one file in its selection fails, mirroring
/// <see cref="EtlPipelines.Abstractions.Configuration.RowErrorAction"/> for files instead of rows.
/// </summary>
public enum FileErrorAction
{
    /// <summary>Stop at the first file that fails. The safe default.</summary>
    Stop = 0,

    /// <summary>
    /// Attempt every file, then fail the stage with one error listing every failure. A run that only
    /// did 497 of 500 files must not report success, so this still fails the stage - it just does not
    /// give up after the first bad file in a large, mostly-independent batch.
    /// </summary>
    Continue,
}
