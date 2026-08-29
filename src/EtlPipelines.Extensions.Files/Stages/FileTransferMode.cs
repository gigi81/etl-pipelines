namespace EtlPipelines.Extensions.Files.Stages;

/// <summary>Whether a <see cref="FileTransferStage"/> copies or moves.</summary>
public enum FileTransferMode
{
    /// <summary>Leaves the source in place.</summary>
    Copy = 0,

    /// <summary>Removes the source once its target is complete.</summary>
    Move,
}
