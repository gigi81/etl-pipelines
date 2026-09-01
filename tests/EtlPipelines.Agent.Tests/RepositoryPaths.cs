namespace EtlPipelines.Agent.Tests;

/// <summary>Finds paths under the repository root from wherever this test assembly happens to run.</summary>
/// <remarks>
/// The same small pattern <c>EtlPipelines.PipelinePackaging.Tests</c> (Phase 1) and
/// <c>EtlPipelines.Server.Database.Tests</c>/<c>EtlPipelines.Server.Tests</c> use for the same
/// reason - duplicated here rather than shared, matching how this repo's test projects generally
/// keep their own copies of small fixtures.
/// </remarks>
internal static class RepositoryPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>
    /// The full path to a project living directly under <c>src/</c>, e.g.
    /// <c>src/EtlPipelines.Samples.ArchiveToDatabase/EtlPipelines.Samples.ArchiveToDatabase.csproj</c>.
    /// </summary>
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
