using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Agents;

namespace EtlPipelines.Server.Tests.Agents;

/// <summary>Fast tests for <see cref="AgentConnectionRegistry"/>'s install-aware agent selection.</summary>
[Category("Server")]
public class AgentConnectionRegistryTests
{
    [Test]
    public async Task Prefers_an_agent_known_to_have_the_version_installed()
    {
        //arrange
        var registry = new AgentConnectionRegistry();
        foreach (var id in new[] { "a", "b", "c" })
        {
            registry.Connect(id, Channel.CreateUnbounded<WorkItem>());
        }

        registry.MarkInstalled("b", "Pkg", "1.0.0");
        registry.MarkInstalled("c", "Pkg", "2.0.0");

        //act
        var found = registry.TryGetConnectedAgentIdFor("Pkg", "2.0.0", out var agentId);

        //assert
        found.Should().BeTrue();
        agentId.Should().Be("c");
    }

    [Test]
    public async Task Falls_back_to_any_connected_agent_when_none_has_the_version()
    {
        //arrange
        var registry = new AgentConnectionRegistry();
        registry.Connect("a", Channel.CreateUnbounded<WorkItem>());

        //act
        var found = registry.TryGetConnectedAgentIdFor("Pkg", "1.0.0", out var agentId);

        //assert
        found.Should().BeTrue();
        agentId.Should().Be("a");
    }

    [Test]
    public async Task Forgets_what_an_agent_had_installed_once_it_disconnects()
    {
        //arrange
        var registry = new AgentConnectionRegistry();
        registry.Connect("a", Channel.CreateUnbounded<WorkItem>());
        registry.MarkInstalled("a", "Pkg", "1.0.0");
        registry.Disconnect("a");
        registry.Connect("a", Channel.CreateUnbounded<WorkItem>());
        registry.Connect("b", Channel.CreateUnbounded<WorkItem>());
        registry.MarkInstalled("b", "Pkg", "1.0.0");

        //act
        registry.TryGetConnectedAgentIdFor("Pkg", "1.0.0", out var agentId);

        //assert
        agentId.Should().Be("b");
    }

    [Test]
    public async Task Finds_nothing_when_no_agent_is_connected()
    {
        //arrange
        var registry = new AgentConnectionRegistry();

        //act
        var found = registry.TryGetConnectedAgentIdFor("Pkg", "1.0.0", out var agentId);

        //assert
        found.Should().BeFalse();
        agentId.Should().BeNull();
    }

    [Test]
    public async Task An_agent_that_connects_again_replaces_its_old_connection_which_is_ended()
    {
        //arrange
        var registry = new AgentConnectionRegistry();
        var old = Channel.CreateUnbounded<WorkItem>();
        var current = Channel.CreateUnbounded<WorkItem>();
        registry.Connect("agent", old);

        //act
        registry.Connect("agent", current);

        //assert - the old queue is closed (its Subscribe call ends), and work goes to the new one
        (await old.Reader.WaitToReadAsync()).Should().BeFalse();
        registry.DispatchExecutePipeline("agent", new ExecutePipelineWorkItem { SessionId = "s" });
        current.Reader.TryRead(out _).Should().BeTrue();
    }

    [Test]
    public async Task The_end_of_an_old_connection_does_not_disconnect_a_newer_one()
    {
        //arrange
        var registry = new AgentConnectionRegistry();
        var old = Channel.CreateUnbounded<WorkItem>();
        var current = Channel.CreateUnbounded<WorkItem>();
        registry.Connect("agent", old);
        registry.MarkInstalled("agent", "Pkg", "1.0.0");
        registry.Connect("agent", current);

        //act
        registry.Disconnect("agent", old);

        //assert
        registry.TryGetAnyConnectedAgentId(out var connected).Should().BeTrue();
        connected.Should().Be("agent");
        registry.HasInstalled("agent", "Pkg", "1.0.0").Should().BeTrue("it is the same agent, still connected");

        //act - the end of the connection it has now
        registry.Disconnect("agent", current);

        //assert
        registry.TryGetAnyConnectedAgentId(out _).Should().BeFalse();
        registry.HasInstalled("agent", "Pkg", "1.0.0").Should().BeFalse();
    }
}
