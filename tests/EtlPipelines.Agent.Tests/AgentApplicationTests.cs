using EtlPipelines.Agent;

namespace EtlPipelines.Agent.Tests;

/// <summary>
/// Smoke test: proves the generic host builds, including the real DI wiring Phase 5 added -
/// building without error is all there is to check here; <c>EtlPipelines.Server.Tests</c>'
/// <c>[Category("Docker")]</c> suite is what proves the agent actually works end to end.
/// </summary>
/// <remarks>Never calls <c>RunAsync()</c> - see <see cref="AgentApplication"/> for why.</remarks>
[Category("Agent")]
public class AgentApplicationTests
{
    [Test]
    public void The_host_builds()
    {
        //act
        using var host = AgentApplication.Build([]);

        //assert
        host.Should().NotBeNull();
    }
}
