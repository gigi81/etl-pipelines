namespace EtlPipelines.Files.Configuration;

/// <summary>What a file stage does when its target already exists.</summary>
public enum OverwritePolicy
{
    /// <summary>
    /// Replace the target. The default: an ETL run gets retried, and a policy that instead refused
    /// would make every retry after a partial failure need manual cleanup. Writes are atomic (see
    /// <see cref="FileWriteOptions"/>), so a consumer never observes a half-written file either way.
    /// </summary>
    Overwrite = 0,

    /// <summary>Leave an existing target alone and move on. Makes a rerun resume without redoing work.</summary>
    Skip,

    /// <summary>
    /// The target must not already exist. The whole set of targets a stage would write is checked
    /// before anything is written, so failing on file 4 of 5 never leaves the first three clobbered.
    /// </summary>
    Fail,
}
