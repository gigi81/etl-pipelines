using System.CommandLine;
using System.IO.Abstractions;

namespace EtlPipelines.Samples.Common;

/// <summary>
/// The directory a sample reads from and writes into.
/// </summary>
/// <remarks>
/// Every sample needs somewhere to put a file, and every sample would otherwise open with the same
/// dozen lines of temp-directory bookkeeping before getting to the pipeline. Registered as a
/// singleton and injected into everything that needs it — the seeder, the command, the pipeline's
/// own registration — which is also what lets a test point a sample at a directory it controls and
/// then look at what turned up in it.
/// </remarks>
public sealed class SampleWorkspace
{
    private readonly IFileSystem _fileSystem;

    /// <summary>Uses a directory the caller already has, creating it if it is not there.</summary>
    public SampleWorkspace(IFileSystem fileSystem, IDirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(directory);

        _fileSystem = fileSystem;
        Directory = directory;
        Directory.Create();
    }

    /// <summary>The option every sample takes, so its output can be put somewhere you can find it.</summary>
    /// <remarks>
    /// Recursive, so it applies to every verb rather than being repeated on each one. Read while the
    /// container is being composed rather than injected from it: the pipeline's own registration
    /// needs the file paths, and that runs before there is a provider to resolve anything from.
    /// </remarks>
    public static Option<string?> WorkDirOption { get; } = new("--work-dir")
    {
        Description = "Directory to read and write in. Defaults to a new directory under the temp path.",
        Recursive = true,
    };

    /// <summary>The directory itself.</summary>
    public IDirectoryInfo Directory { get; }

    /// <summary>A file in the workspace, whether or not it exists yet.</summary>
    public IFileInfo File(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _fileSystem.FileInfo.New(_fileSystem.Path.Combine(Directory.FullName, name));
    }

    /// <summary>Builds the workspace a command line run should use.</summary>
    /// <param name="result">The parsed command line, which may carry <c>--work-dir</c>.</param>
    /// <param name="name">Appears in the default directory name, so a leftover can be traced back.</param>
    public static SampleWorkspace ForCommandLine(ParseResult result, string name)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var fileSystem = new FileSystem();
        var path = result.GetValue(WorkDirOption)
            ?? Path.Combine(Path.GetTempPath(), $"etl-sample-{name}-{Guid.NewGuid():N}");

        return new SampleWorkspace(fileSystem, fileSystem.DirectoryInfo.New(path));
    }
}
