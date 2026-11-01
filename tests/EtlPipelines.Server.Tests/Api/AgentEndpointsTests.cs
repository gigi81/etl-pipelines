using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Contracts;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>Removing agents: the clean-up for registrations a restarted or retired agent leaves behind.</summary>
[Category("Server")]
public class AgentEndpointsTests
{
    [Test]
    public async Task An_offline_agent_can_be_removed_and_is_no_longer_listed_or_given_work()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var stale = await host.SeedAgentAsync("old-agent", status: "Offline");
        await host.SeedAgentAsync("current-agent");
        host.Connections.Connect(stale.ToString(), Channel.CreateUnbounded<WorkItem>());

        //act
        var response = await host.Client.DeleteAsync($"/api/agents/{stale}");

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await host.GetAsync<List<AgentDto>>("/api/agents")).Select(a => a.MachineName).Should().Equal("current-agent");
        host.Connections.TryGetAnyConnectedAgentId(out _).Should().BeFalse("it is not dispatched to any more");
    }

    [Test]
    public async Task An_agent_that_is_online_is_not_removed()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var online = await host.SeedAgentAsync("agent-01");

        //act
        var response = await host.Client.DeleteAsync($"/api/agents/{online}");

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Detail(response)).Should().Contain("online");
        (await host.GetAsync<List<AgentDto>>("/api/agents")).Should().ContainSingle();
    }

    [Test]
    public async Task An_agent_with_a_run_in_progress_is_not_removed()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var agentId = await host.SeedAgentAsync("agent-01", status: "Offline");
        var pipelineId = await host.SeedPipelineAsync("p");
        await host.SeedRunAsync(pipelineId, "Running", agentId);

        //act
        var response = await host.Client.DeleteAsync($"/api/agents/{agentId}");

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Detail(response)).Should().Contain("runs in progress");
    }

    [Test]
    public async Task An_unknown_agent_is_a_404()
    {
        await using var host = await ApiTestHost.StartAsync();

        (await host.Client.DeleteAsync($"/api/agents/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Removing_an_agent_is_pushed_to_whoever_is_watching()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var stale = await host.SeedAgentAsync("old-agent", status: "Offline");
        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        using var events = new SseReader(await response.Content.ReadAsStreamAsync());
        (await events.ReadWithinAsync(TimeSpan.FromSeconds(5))).Type.Should().Be("ready");

        //act
        await host.Client.DeleteAsync($"/api/agents/{stale}");

        //assert
        var (type, data) = await events.ReadWithinAsync(TimeSpan.FromSeconds(5));
        type.Should().Be("agent-removed");
        JsonDocument.Parse(data).RootElement.GetProperty("id").GetGuid().Should().Be(stale);
    }

    private static async Task<string> Detail(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
}
