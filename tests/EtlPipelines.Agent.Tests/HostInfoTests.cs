using System.Net;
using EtlPipelines.AgentExecution.V1;

namespace EtlPipelines.Agent.Tests;

/// <summary>What an agent tells the server about its host when it registers.</summary>
[Category("Agent")]
public class HostInfoTests
{
    [Test]
    public async Task Describe_fills_in_the_host_details_the_agents_page_shows()
    {
        //arrange
        var request = new RegisterAgentRequest();
        var before = DateTime.UtcNow;

        //act
        HostInfo.Describe(request);

        //assert
        request.OsDescription.Should().NotBeNullOrWhiteSpace();
        request.OsArchitecture.Should().NotBeNullOrWhiteSpace();
        request.ProcessorCount.Should().BeGreaterThan(0);
        request.StartedAt.ToDateTime().Should().BeBefore(before.AddSeconds(1), "the agent process started before it registered");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IpAddresses_are_unicast_non_loopback_and_not_link_local()
    {
        //act
        var addresses = HostInfo.GetIpAddresses().Select(IPAddress.Parse).ToList();

        //assert
        addresses.Should().OnlyContain(address => !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal);
        addresses.Should().OnlyHaveUniqueItems();
        await Task.CompletedTask;
    }
}
