namespace EtlPipelines.PipelinePackaging.Tests;

/// <summary>Finds paths under the repository root from wherever this test assembly happens to run.</summary>
/// <remarks>
/// Nothing elsewhere in the repo needed this before - every other test either runs a sample in-process
/// (no path to a .csproj required at all) or reads fixtures that ship inside its own test project. This
/// project is the first to need the source tree's own layout at test time, since it hands
/// <c>dotnet pack</c> a real project path.
/// </remarks>
internal static class RepositoryPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>
    /// The full path to a project living directly under <c>src/</c>, e.g.
    /// <c>src/EtlPipelines.Samples.ArchiveToDatabase/EtlPipelines.Samples.ArchiveToDatabase.csproj</c>.
    /// </summary>
    /// <param name="projectName">
    /// The project's directory and assembly name, identical by convention everywhere in this repo -
    /// no separate "which folder holds it" lookup is needed.
    /// </param>
    public static string SourceProjectFile(string projectName) =>
        Path.Combine(Root.Value, "src", projectName, $"{projectName}.csproj");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("EtlPipelines.slnx").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find EtlPipelines.slnx above {AppContext.BaseDirectory} - " +
            "is this test running from inside a checkout of the repository?");
    }
}
