using System.Diagnostics;

namespace EtlPipelines.Agent.Tests;

/// <summary>Fast test for <see cref="ResourceMonitor"/> - SERVER.md Phase 5's own instruction.</summary>
[Category("Agent")]
public class ResourceMonitorTests
{
    [Test]
    public async Task Samples_a_real_process_until_it_exits()
    {
        //arrange - "dotnet --info" takes comfortably longer than the sampling interval below (it
        // loads the whole SDK resolver before printing anything), so this reliably produces more
        // than one sample without needing a purpose-built long-running child process.
        using var process = Process.Start(new ProcessStartInfo("dotnet", "--info") { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start 'dotnet --info'.");

        //act
        var samples = new List<ResourceSample>();
        await foreach (var sample in ResourceMonitor.SampleAsync(process, TimeSpan.FromMilliseconds(10)))
        {
            samples.Add(sample);
        }

        await process.WaitForExitAsync();

        //assert
        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(sample => sample.WorkingSetBytes > 0, "a running process always has some working set");
        samples.Should().OnlyContain(sample => sample.CpuPercent >= 0, "CPU percent should never come out negative");
    }
}
