using System.IO.Abstractions;
using EtlPipelines.Samples.Common;

namespace EtlPipelines.Samples.Tests;

/// <summary>A real directory on a real disk for a sample to write into, deleted afterwards.</summary>
/// <remarks>
/// Deliberately the real filesystem. These tests exist to prove the samples work as written, and a
/// sample that only ran against an in-memory filesystem would not be evidence of that.
/// </remarks>
public sealed class SampleScratch : IDisposable
{
    private readonly IFileSystem _fileSystem = new FileSystem();

    public SampleScratch(string name)
    {
        Directory = _fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-samples-{name}-{Guid.NewGuid():N}"));
        Directory.Create();

        Workspace = new SampleWorkspace(Directory);
    }

    public IDirectoryInfo Directory { get; }

    /// <summary>The scratch directory as a sample sees it.</summary>
    public SampleWorkspace Workspace { get; }

    public IFileInfo File(string name) => Workspace.File(name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test over.
        }
    }
}
