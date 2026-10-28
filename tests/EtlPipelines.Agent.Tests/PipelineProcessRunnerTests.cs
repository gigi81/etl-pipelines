namespace EtlPipelines.Agent.Tests;

/// <summary>
/// Fast test for <see cref="PipelineProcessRunner.RunAsync"/>'s resource sampling, against a
/// throwaway script standing in for a pipeline shim - it ignores the <c>run</c> arguments, stays
/// alive long enough to be sampled, and exits with a known code.
/// </summary>
[Category("Agent")]
public class PipelineProcessRunnerTests
{
    [Test]
    public async Task RunAsync_samples_the_process_while_it_runs_and_returns_its_exit_code()
    {
        //arrange
        var directory = Directory.CreateTempSubdirectory("etl-runner-");
        try
        {
            var shimPath = WriteShim(directory.FullName);
            var samples = new List<ResourceSample>();

            //act
            var exitCode = await PipelineProcessRunner.RunAsync(
                shimPath,
                "any-pipeline",
                Guid.NewGuid().ToString(),
                "http://localhost:5000",
                TimeSpan.FromMilliseconds(50),
                (sample, _) =>
                {
                    lock (samples)
                    {
                        samples.Add(sample);
                    }

                    return Task.CompletedTask;
                },
                CancellationToken.None);

            //assert
            exitCode.Should().Be(3);
            samples.Should().NotBeEmpty();
            samples.Should().OnlyContain(sample => sample.WorkingSetBytes > 0);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string WriteShim(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmdPath = Path.Combine(directory, "shim.cmd");
            File.WriteAllText(cmdPath, "@ping -n 3 127.0.0.1 >nul\r\n@exit /b 3\r\n");
            return cmdPath;
        }

        var shPath = Path.Combine(directory, "shim.sh");
        File.WriteAllText(shPath, "#!/bin/sh\nsleep 1\nexit 3\n");
        File.SetUnixFileMode(shPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shPath;
    }
}
