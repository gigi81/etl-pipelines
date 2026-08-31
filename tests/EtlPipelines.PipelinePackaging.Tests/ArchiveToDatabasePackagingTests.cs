using CliWrap;
using CliWrap.Buffered;

namespace EtlPipelines.PipelinePackaging.Tests;

/// <summary>
/// Proves a sample survives being packed and installed as a real <c>dotnet tool</c>, not merely run
/// in-process the way every other sample test does.
/// </summary>
/// <remarks>
/// <para>
/// <c>EtlPipelines.Samples.Tests</c> proves each sample's own behaviour, in-process
/// (<c>SampleTests</c>) and through its command-line wiring (<c>SampleCliTests</c>, still calling
/// <c>Program.RunAsync(string[])</c> directly). Neither proves the one thing SERVER.md's Phase 1 -
/// "Pipeline packaging: real <c>dotnet tool</c> packages, proven on the samples" - actually depends on
/// everywhere downstream of it: that <c>dotnet pack</c> produces something <c>dotnet tool install</c>
/// truly installs, and that the generated shim is a real, independently runnable executable rather than
/// something only reachable via <c>dotnet run</c>. This is that proof, and - so far as a repo-wide
/// search turned up when this was written - the first test here to spawn and read a genuine OS process
/// rather than call into the same AppDomain: a real <c>dotnet pack</c>, a real <c>dotnet tool install</c>,
/// then the installed shim itself, via <c>CliWrap</c> - the already centrally-versioned dependency that,
/// before <c>EtlPipelines.Extensions.Cli</c> landed, had no consumer anywhere in the repo.
/// </para>
/// <para>
/// <c>ArchiveToDatabase</c> specifically, per SERVER.md's own reasoning: it is the richest of the six
/// packable samples, registering two separately-nameable pipelines (<c>build-feed</c>, <c>archive</c>)
/// with a real dependency between them, so a single install-and-run round trip exercises more of the
/// packaged shim's behaviour than any of the others would.
/// </para>
/// <para>
/// Slow - two real MSBuild/NuGet round trips (pack, then install) - so this is tagged
/// <c>[Category("Packaging")]</c> and excluded from the fast per-PR <c>ci.yml</c> run the same way
/// <c>[Category("Docker")]</c> tests are; <c>integration-tests.yml</c> runs it in its own sequential
/// step instead.
/// </para>
/// </remarks>
[Category("Packaging")]
public class ArchiveToDatabasePackagingTests
{
    // Mirrors EtlPipelines.Samples.ArchiveToDatabase.Pipeline.SeedName/Name/ArchiveFile. Kept as
    // literals rather than a ProjectReference to that project deliberately - see this test project's
    // .csproj for why: the sample is proven here purely as an external process, never loaded into this
    // assembly.
    private const string PackageId = "EtlPipelines.Samples.ArchiveToDatabase";
    private const string SeedPipelineName = "build-feed";
    private const string JobPipelineName = "archive";
    private const string ArchiveFile = "feed.zip";

    [Test]
    public async Task Packed_and_installed_tool_lists_and_runs_its_pipelines()
    {
        //arrange
        var projectPath = RepositoryPaths.SourceProjectFile(PackageId);
        var work = Directory.CreateTempSubdirectory("EtlPipelines.PipelinePackaging.");

        try
        {
            var packagesDirectory = Path.Combine(work.FullName, "packages");
            var toolDirectory = Path.Combine(work.FullName, "tool-install");
            var scratchDirectory = Directory.CreateDirectory(Path.Combine(work.FullName, "scratch")).FullName;

            //act - pack
            var pack = await Cli.Wrap("dotnet")
                .WithArguments(["pack", projectPath, "--configuration", "Release", "--output", packagesDirectory])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            pack.ExitCode.Should().Be(0, $"dotnet pack should succeed: {Tail(pack)}");

            //act - install
            // --prerelease: Nerdbank.GitVersioning stamps every non-tagged build with a prerelease
            // suffix (e.g. "1.0.0-gb30a5af9d9"), which is exactly the version this pack step just
            // produced - `dotnet tool install` excludes prerelease versions unless asked for one
            // explicitly, and without this flag it reports the package as "not found" rather than as
            // prerelease-and-skipped.
            var install = await Cli.Wrap("dotnet")
                .WithArguments([
                    "tool", "install",
                    "--tool-path", toolDirectory,
                    "--add-source", packagesDirectory,
                    "--prerelease",
                    PackageId,
                ])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            install.ExitCode.Should().Be(0, $"dotnet tool install should succeed: {Tail(install)}");

            var shim = FindInstalledShim(toolDirectory);

            //act - list
            var list = await Cli.Wrap(shim)
                .WithArguments(["list"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            //assert - list: the same contract ListPipelinesHandler/Writer.WriteLine already guarantees
            // for the in-process case, now proven to survive being installed as a real tool.
            list.ExitCode.Should().Be(0, $"list should succeed: {Tail(list)}");
            var names = list.StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            names.Should().Equal([SeedPipelineName, JobPipelineName]);

            //act - run
            var run = await Cli.Wrap(shim)
                .WithArguments(["run", SeedPipelineName, "--work-dir", scratchDirectory])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            //assert - run
            run.ExitCode.Should().Be(0, $"run {SeedPipelineName} should succeed: {Tail(run)}");
            File.Exists(Path.Combine(scratchDirectory, ArchiveFile))
                .Should().BeTrue($"{SeedPipelineName} bundles the five files into a real zip");
        }
        finally
        {
            try
            {
                Directory.Delete(work.FullName, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over - matches SampleCliTests'
                // own Cleanup convention.
            }
        }
    }

    /// <summary>
    /// <c>dotnet tool install --tool-path</c> drops the generated shim directly in the tool-path
    /// directory, alongside a <c>.store</c> subdirectory holding the actual package contents. The
    /// shim's exact name is deliberately not hardcoded here rather than assumed - empirically (macOS,
    /// .NET 10 SDK) it keeps <see cref="PackageId"/>'s own casing rather than lowercasing it, which is
    /// the opposite of what SERVER.md's Phase 1 plan guessed before this test existed to check; other
    /// platforms/SDK versions are not something worth re-guessing about either when the directory
    /// listing already answers it unambiguously. The extension is a platform detail too (none on
    /// Linux/macOS, <c>.exe</c> on Windows).
    /// </summary>
    private static string FindInstalledShim(string toolDirectory)
    {
        var shim = Directory.EnumerateFiles(toolDirectory)
            .SingleOrDefault(path => !Path.GetFileName(path).StartsWith('.'));

        shim.Should().NotBeNull(
            $"dotnet tool install should have dropped exactly one shim directly under {toolDirectory}");

        return shim!;
    }

    private static string Tail(BufferedCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() : "(no output)";
    }
}
