using System.Net;
using Google.Protobuf.WellKnownTypes;

namespace EtlPipelines.Agent.Tests;

/// <summary>What an agent tells the server about its host when it registers.</summary>
[Category("Agent")]
public class HostInfoTests
{
    [Test]
    public async Task Describe_gives_the_host_details_the_agents_page_shows()
    {
        //arrange
        var before = DateTimeOffset.UtcNow;

        //act
        var info = HostInfo.Describe();

        //assert
        info.Fields["osDescription"].StringValue.Should().NotBeNullOrWhiteSpace();
        info.Fields["osArchitecture"].StringValue.Should().NotBeNullOrWhiteSpace();
        info.Fields["processorCount"].NumberValue.Should().BeGreaterThan(0);
        info.Fields["ipAddresses"].KindCase.Should().Be(Value.KindOneofCase.ListValue, "a host can have several addresses");
        var startedAt = info.Fields["startedAtUtc"].StringValue;
        startedAt.Should().EndWith("Z", "timestamps are always UTC");
        DateTimeOffset.Parse(startedAt).Should().BeBefore(before.AddSeconds(1), "the process started before it registered");
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
