namespace EtlPipelines.Files.Configuration;

/// <summary>Settings shared by every file stage that writes a target - locally or, for the Sftp
/// satellite, remotely.</summary>
public abstract class FileWriteOptions : FileStageOptions
{
    /// <summary>What to do when a target already exists. Defaults to <see cref="OverwritePolicy.Overwrite"/>.</summary>
    public OverwritePolicy Overwrite { get; set; } = OverwritePolicy.Overwrite;

    /// <summary>Whether a missing target directory is created. Defaults to <see langword="true"/>.</summary>
    public bool CreateTargetDirectory { get; set; } = true;
}
