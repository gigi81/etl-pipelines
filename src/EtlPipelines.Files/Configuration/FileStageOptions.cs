namespace EtlPipelines.Files.Configuration;

/// <summary>Settings shared by every file stage in this package and its Http and Sftp satellites.</summary>
public abstract class FileStageOptions
{
    /// <summary>
    /// What the stage is called in the run's report, traces and metrics. Defaults to a description
    /// derived from what the stage does.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The key later stages read this stage's output back under, through
    /// <see cref="EtlPipelines.Files.FileItems"/>. Defaults to the stage's own <see cref="Name"/>.
    /// </summary>
    public string? PublishAs { get; set; }

    /// <summary>What happens when one file in a multi-file selection fails. Defaults to <see cref="FileErrorAction.Stop"/>.</summary>
    public FileErrorAction OnFileError { get; set; } = FileErrorAction.Stop;

    /// <summary>
    /// How many files a multi-file stage works on at once. Defaults to 1: sequential means a server
    /// on the other end is never surprised by concurrent requests, and "file 3 of 5 failed" stays
    /// unambiguous. Raise it for many small, independent files.
    /// </summary>
    public int MaxConcurrency { get; set; } = 1;
}
