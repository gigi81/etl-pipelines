using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent.Tests;

/// <summary>
/// Fast test for <see cref="PackageInstaller"/> (and, via its output,
/// <see cref="PipelineProcessRunner.ListPipelineNamesAsync"/>) against a real packed sample -
/// SERVER.md Phase 5's own instruction. Reuses <c>EtlPipelines.Samples.ArchiveToDatabase</c>, the
/// same sample <c>EtlPipelines.PipelinePackaging.Tests</c> (Phase 1) picked for the same reason:
/// "the richest - two registered pipelines, a real dependency graph" - no new fixture needed.
/// </summary>
[Category("Agent")]
public class PackageInstallerTests
{
    private const string PackageId = "EtlPipelines.Samples.ArchiveToDatabase";

    [Test]
    public async Task Install_produces_a_runnable_shim_reporting_both_the_sample_s_pipelines()
    {
        //arrange
        var work = Directory.CreateTempSubdirectory("EtlPipelines.Agent.Tests.");

        try
        {
            var packagesDirectory = Path.Combine(work.FullName, "packages");
            var cacheDirectory = Path.Combine(work.FullName, "agent-cache");
            var version = await PackAsync(packagesDirectory);

            var installer = new PackageInstaller(Options.Create(new AgentOptions { CacheDirectory = cacheDirectory }));

            //act
            var shimPath = await installer.InstallAsync(PackageId, version, [packagesDirectory], CancellationToken.None);

            //assert
            File.Exists(shimPath).Should().BeTrue($"dotnet tool install should have dropped a shim at {shimPath}");

            var names = await PipelineProcessRunner.ListPipelineNamesAsync(shimPath, CancellationToken.None);
            names.Should().BeEquivalentTo(["build-feed", "archive"]);
        }
        finally
        {
            try
            {
                Directory.Delete(work.FullName, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    /// <summary>Packs the sample and returns the version <c>dotnet pack</c> gave it.</summary>
    private static async Task<string> PackAsync(string outputDirectory)
    {
        var projectPath = RepositoryPaths.SourceProjectFile(PackageId);

        BufferedCommandResult pack;
        try
        {
            pack = await Cli.Wrap("dotnet")
                .WithArguments(["pack", projectPath, "--configuration", "Release", "--output", outputDirectory])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException("'dotnet' could not be started.", exception);
        }

        pack.ExitCode.Should().Be(0, $"dotnet pack should succeed: {Tail(pack)}");

        var nupkg = Directory.GetFiles(outputDirectory, "*.nupkg").Single();
        var fileName = Path.GetFileNameWithoutExtension(nupkg);

        // "<PackageId>.<version>" -> "<version>" - PackageId is the fixed prefix, so this is
        // simpler and more robust than parsing NuGetVersion out of an arbitrary file name.
        return fileName[(PackageId.Length + 1)..];
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
