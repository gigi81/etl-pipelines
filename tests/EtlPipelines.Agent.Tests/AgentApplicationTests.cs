using EtlPipelines.Agent;

namespace EtlPipelines.Agent.Tests;

/// <summary>
/// Phase 2 smoke test: proves the generic host builds - no real behaviour exists yet (see
/// <see cref="AgentApplication"/>'s own remarks), so building without error is all there is to
/// check.
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
