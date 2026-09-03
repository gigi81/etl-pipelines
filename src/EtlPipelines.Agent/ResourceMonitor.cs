using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace EtlPipelines.Agent;

/// <summary>
/// Samples a running process's CPU/memory on a timer - <c>Process.TotalProcessorTime</c>/
/// <c>WorkingSet64</c>, no new dependency. Phase 6/8 is what wires these samples into
/// <c>ReportExecutionStatus</c>; this phase only builds and unit-tests the sampling itself -
/// nothing calls this class yet.
/// </summary>
public static class ResourceMonitor
{
    /// <summary>
    /// Samples <paramref name="process"/> every <paramref name="interval"/> until it exits or
    /// <paramref name="cancellationToken"/> fires. <see cref="ResourceSample.CpuPercent"/> is
    /// normalized against <see cref="Environment.ProcessorCount"/>, so 100% means "using every
    /// core", not "using one core fully".
    /// </summary>
    public static async IAsyncEnumerable<ResourceSample> SampleAsync(
        Process process, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(interval);
        var previousCpuTime = process.TotalProcessorTime;
        var previousSampledAt = DateTimeOffset.UtcNow;

        while (!process.HasExited && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            process.Refresh();
            if (process.HasExited)
            {
                yield break;
            }

            var sampledAt = DateTimeOffset.UtcNow;
            var cpuTime = process.TotalProcessorTime;
            var workingSet = process.WorkingSet64;

            // HasExited and WorkingSet64 each make their own OS call rather than sharing one
            // atomic snapshot from Refresh() above, so a process that finishes in the gap between
            // them can still read HasExited == false yet WorkingSet64 == 0 right after - caught
            // for real, consistently, sampling a short-lived `dotnet --info` child process at a
            // 10ms interval. A still-alive process never legitimately has a zero working set, so
            // treat that reading the same as having already exited rather than reporting a
            // nonsensical zero-memory sample.
            if (workingSet <= 0)
            {
                yield break;
            }

            var elapsedWallClock = sampledAt - previousSampledAt;

            var cpuPercent = elapsedWallClock.TotalMilliseconds > 0
                ? (cpuTime - previousCpuTime).TotalMilliseconds / elapsedWallClock.TotalMilliseconds / Environment.ProcessorCount * 100.0
                : 0.0;

            yield return new ResourceSample(sampledAt, cpuPercent, workingSet);

            previousCpuTime = cpuTime;
            previousSampledAt = sampledAt;
        }
    }
}

/// <summary>One CPU/memory sample - mirrors <c>AgentResourceSamples</c> (Phase 3)/<c>ReportExecutionStatusRequest</c> (agent_execution.v1).</summary>
public sealed record ResourceSample(DateTimeOffset SampledAt, double CpuPercent, long WorkingSetBytes);
