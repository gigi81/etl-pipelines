using System.IO.Abstractions;

namespace EtlPipelines.Samples.Common;

/// <summary>
/// The directory a sample reads from and writes into.
/// </summary>
/// <remarks>
/// Every sample needs somewhere to put a file, and every sample would otherwise open with the same
/// dozen lines of temp-directory bookkeeping before getting to the pipeline. It is also the seam the
/// tests use: they hand a sample a directory they control and then look at what turned up in it.
/// </remarks>
public sealed class SampleWorkspace
{
    /// <summary>Uses a directory the caller already has. This is what the tests do.</summary>
    public SampleWorkspace(IDirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        Directory = directory;
        Directory.Create();
    }

    /// <summary>A fresh directory under the system temp path, on the real filesystem.</summary>
    /// <param name="name">Appears in the directory name, so a leftover can be traced to its sample.</param>
    public static SampleWorkspace CreateTemporary(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var fileSystem = new FileSystem();

        return new SampleWorkspace(fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-sample-{name}-{Guid.NewGuid():N}")));
    }

    /// <summary>The directory itself.</summary>
    public IDirectoryInfo Directory { get; }

    /// <summary>The filesystem the directory belongs to.</summary>
    public IFileSystem FileSystem => Directory.FileSystem;

    /// <summary>A file in the workspace, whether or not it exists yet.</summary>
    public IFileInfo File(string name) => Directory.File(name);
}
