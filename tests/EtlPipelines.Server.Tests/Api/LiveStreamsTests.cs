using System.Net;
using System.Text.Json;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>The server-sent-event streams: pushed from the in-memory state, never polled from the database.</summary>
[Category("Server")]
public class LiveStreamsTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Test]
    public async Task A_run_stream_sends_the_run_right_away_and_again_each_time_it_changes_until_it_ends()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var runId = await host.SeedRunAsync(pipelineId, "Running");

        using var response = await host.Client.GetAsync($"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        using var events = new SseReader(await response.Content.ReadAsStreamAsync());

        //act + assert
        var first = Parse(await events.ReadWithinAsync(Soon));
        first.Run.Status.Should().Be("Running");
        first.Stages.Should().BeEmpty();

        await host.Live.StageCompletedAsync(runId, new StageDto(0, "read", 0, 5, 0, 10, null, null), CancellationToken.None);

        var afterStage = Parse(await events.ReadWithinAsync(Soon));
        afterStage.Stages.Select(s => s.Name).Should().Equal("read");

        await host.Live.ResourceSampledAsync(runId, Guid.NewGuid(), 12.5, 2048, CancellationToken.None);

        Parse(await events.ReadWithinAsync(Soon)).ResourceSamples.Should().ContainSingle(sample => sample.CpuPercent == 12.5);

        await host.Live.FinishRunAsync(runId, "Succeeded", 0, 0, 0, 0, CancellationToken.None);

        Parse(await events.ReadWithinAsync(Soon)).Run.Status.Should().Be("Succeeded");
        using var cancel = new CancellationTokenSource(Soon);
        (await events.ReadAsync(cancel.Token)).Should().BeNull("the stream ends once the run has finished");
    }

    [Test]
    public async Task A_run_stream_does_not_look_at_the_database_so_a_change_made_behind_the_servers_back_is_not_seen()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var runId = await host.SeedRunAsync(pipelineId, "Running");

        using var response = await host.Client.GetAsync($"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        using var events = new SseReader(await response.Content.ReadAsStreamAsync());
        await events.ReadWithinAsync(Soon);

        //act - the database changes, but nothing tells the live state
        await using (var context = host.NewContext())
        {
            context.StageResults.Add(new Database.Entities.StageResult
            {
                Id = Guid.NewGuid(), RunId = runId, Sequence = 0, Name = "read", RowsIn = 0, RowsOut = 5, RowsFailed = 0, ElapsedMs = 10,
            });
            await context.SaveChangesAsync();
        }

        //assert - the old implementation re-read the database every second and would have sent this
        (await events.StaysQuietForAsync(TimeSpan.FromSeconds(2.5))).Should().BeTrue();
    }

    [Test]
    public async Task A_finished_run_streams_once_and_ends()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var runId = await host.SeedRunAsync(pipelineId, "Succeeded", requestedAt: DateTime.UtcNow.AddDays(-2), stages: [("read", false)]);

        //act
        using var response = await host.Client.GetAsync($"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        using var events = new SseReader(await response.Content.ReadAsStreamAsync());
        var only = Parse(await events.ReadWithinAsync(Soon));

        //assert
        only.Run.Status.Should().Be("Succeeded");
        only.Stages.Should().HaveCount(1);
        using var cancel = new CancellationTokenSource(Soon);
        (await events.ReadAsync(cancel.Token)).Should().BeNull();
    }

    [Test]
    public async Task The_event_feed_pushes_a_run_and_its_agent_when_a_run_is_started()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("csv-to-db");
        var agentId = await host.SeedAgentAsync();
        host.Connections.Connect(agentId.ToString(), Channel.CreateUnbounded<WorkItem>());

        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        using var events = new SseReader(await response.Content.ReadAsStreamAsync());
        (await events.ReadWithinAsync(Soon)).Type.Should().Be("ready");

        //act
        var trigger = await host.Client.PostAsync($"/api/pipelines/{pipelineId}/runs", content: null);
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);

        //assert
        var received = new Dictionary<string, JsonElement>();
        while (received.Count < 2)
        {
            var (type, data) = await events.ReadWithinAsync(Soon);
            received[type] = JsonDocument.Parse(data).RootElement.Clone();
        }

        received["run"].GetProperty("pipelineName").GetString().Should().Be("csv-to-db");
        received["run"].GetProperty("status").GetString().Should().Be("Dispatched");
        received["agent"].GetProperty("machineName").GetString().Should().Be("agent-01");
        received["agent"].GetProperty("activeRuns").GetArrayLength().Should().Be(1, "the agent is now working on it");
    }

    [Test]
    public async Task The_event_feed_pushes_a_heartbeat_and_a_change_to_the_installed_pipelines()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var agentId = await host.SeedAgentAsync();

        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        using var events = new SseReader(await response.Content.ReadAsStreamAsync());
        (await events.ReadWithinAsync(Soon)).Type.Should().Be("ready");

        //act
        await host.Live.AgentHeartbeatAsync(agentId, CancellationToken.None);
        var heartbeatAt = DateTime.UtcNow;

        host.Live.PipelinesChanged();

        //assert
        var agent = await events.ReadWithinAsync(Soon);
        agent.Type.Should().Be("agent");
        JsonDocument.Parse(agent.Data).RootElement.GetProperty("lastHeartbeatAtUtc").GetDateTime().Should().BeCloseTo(heartbeatAt, TimeSpan.FromSeconds(5));
        (await events.ReadWithinAsync(Soon)).Type.Should().Be("pipelines");
    }

    [Test]
    public async Task Closing_a_stream_stops_listening()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        var events = new SseReader(await response.Content.ReadAsStreamAsync());
        (await events.ReadWithinAsync(Soon)).Type.Should().Be("ready");
        host.Live.SubscriberCount.Should().Be(1);

        //act
        events.Dispose();
        response.Dispose();

        //assert
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (host.Live.SubscriberCount > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        host.Live.SubscriberCount.Should().Be(0, "a closed browser tab must not leave a listener behind");
    }

    private static RunDetailDto Parse((string Type, string Data) received)
    {
        received.Type.Should().Be("run");
        return JsonSerializer.Deserialize<RunDetailDto>(received.Data, ApiTestHost.Json)!;
    }
}
