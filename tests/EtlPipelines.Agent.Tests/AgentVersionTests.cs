using EtlPipelines.Agent;

namespace EtlPipelines.Agent.Tests;

/// <summary><see cref="AgentVersion"/>: what the agents page shows as the agent's version.</summary>
[Category("Agent")]
public class AgentVersionTests
{
    [Test]
    public void The_commit_is_kept_short()
    {
        AgentVersion.Format("0.2.0+1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b", new Version(0, 0, 0, 0)).Should().Be("0.2.0+1a2b3c4");
    }

    [Test]
    public void A_version_with_no_commit_is_shown_as_it_is_and_a_prerelease_keeps_its_suffix()
    {
        AgentVersion.Format("0.2.0", new Version(0, 0, 0, 0)).Should().Be("0.2.0");
        AgentVersion.Format("0.3.1-g1a2b3c4", null).Should().Be("0.3.1-g1a2b3c4");
        AgentVersion.Format("0.2.0+", null).Should().Be("0.2.0");
    }

    [Test]
    public void Without_an_informational_version_the_assembly_version_is_used_and_without_either_zero()
    {
        AgentVersion.Format(null, new Version(1, 2, 3, 4)).Should().Be("1.2.3.4");
        AgentVersion.Format("  ", null).Should().Be("0.0.0");
    }

    [Test]
    public void The_running_agent_does_not_report_the_empty_assembly_version()
    {
        AgentVersion.Current.Should().NotBe("0.0.0.0").And.NotBeNullOrWhiteSpace();
    }
}
