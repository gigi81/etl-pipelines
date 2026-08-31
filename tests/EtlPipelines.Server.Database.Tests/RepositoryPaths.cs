namespace EtlPipelines.Server.Database.Tests;

/// <summary>Finds paths under the repository root from wherever this test assembly happens to run.</summary>
/// <remarks>
/// The same small pattern <c>EtlPipelines.PipelinePackaging.Tests</c> uses for the same reason -
/// duplicated here rather than shared, matching how this repo's test projects generally keep their
/// own copies of small fixtures (see <see cref="DatabaseFixture{TContainer}"/>'s own remarks).
/// </remarks>
internal static class RepositoryPaths
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>The full path to <c>db</c> at the repository root - dbdeploy's own <c>--path</c>.</summary>
    public static string DbDirectory => Path.Combine(Root.Value, "db");

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
