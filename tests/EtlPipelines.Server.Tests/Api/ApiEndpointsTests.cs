using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Contracts;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>The REST endpoints the WebUI (<c>web/</c>) is built on.</summary>
[Category("Server")]
public class ApiEndpointsTests
{
    [Test]
    public async Task Pipelines_lists_every_installed_pipeline_with_its_last_run_and_how_many_are_running()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var busy = await host.SeedPipelineAsync("busy", "Pkg.Busy", "2.0.0");
        await host.SeedPipelineAsync("idle");
        await host.SeedRunAsync(busy, "Succeeded", requestedAt: DateTime.UtcNow.AddMinutes(-10));
        await host.SeedRunAsync(busy, "Running", requestedAt: DateTime.UtcNow.AddMinutes(-1));

        //act
        var pipelines = await host.GetAsync<List<PipelineDto>>("/api/pipelines");

        //assert
        pipelines.Select(p => p.Name).Should().Equal("busy", "idle");
        pipelines[0].PackageId.Should().Be("Pkg.Busy");
        pipelines[0].PackageVersion.Should().Be("2.0.0");
        pipelines[0].ActiveRuns.Should().Be(1);
        pipelines[0].LastRun!.Status.Should().Be("Running");
        pipelines[1].ActiveRuns.Should().Be(0);
        pipelines[1].LastRun.Should().BeNull();
    }

    [Test]
    public async Task Triggering_a_run_creates_it_and_sends_it_to_a_connected_agent()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("csv-to-db");
        var agentId = await host.SeedAgentAsync();
        var channel = Channel.CreateUnbounded<WorkItem>();
        host.Connections.Connect(agentId.ToString(), channel);

        //act
        var response = await host.Client.PostAsync($"/api/pipelines/{pipelineId}/runs", content: null);

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = (await response.Content.ReadFromJsonAsync<TriggerRunResponse>(ApiTestHost.Json))!;
        response.Headers.Location!.ToString().Should().Be($"/api/runs/{body.RunId}");

        // The run exists in memory, which is what the API reads - and is not in the database yet.
        var run = host.Live.TryGetRunSummary(body.RunId)!;
        run.PipelineId.Should().Be(pipelineId);
        run.AgentId.Should().Be(agentId);
        run.Status.Should().Be("Dispatched");
        await using (var context = host.NewContext())
        {
            (await context.Runs.CountAsync()).Should().Be(0);
        }

        channel.Reader.TryRead(out var workItem).Should().BeTrue();
        workItem!.ExecutePipeline.SessionId.Should().Be(body.RunId.ToString());
        workItem.ExecutePipeline.PipelineName.Should().Be("csv-to-db");
    }

    [Test]
    public async Task Triggering_an_unknown_pipeline_is_a_404()
    {
        await using var host = await ApiTestHost.StartAsync();

        var response = await host.Client.PostAsync($"/api/pipelines/{Guid.NewGuid()}/runs", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Triggering_a_run_with_no_agent_connected_is_a_409_that_says_why()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("csv-to-db");

        //act
        var response = await host.Client.PostAsync($"/api/pipelines/{pipelineId}/runs", content: null);

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString().Should().Contain("none is connected");
    }

    [Test]
    public async Task Runs_can_be_filtered_to_the_ones_in_flight_or_the_finished_ones_newest_first()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var oldFinished = await host.SeedRunAsync(pipelineId, "Failed", requestedAt: DateTime.UtcNow.AddHours(-2));
        var newFinished = await host.SeedRunAsync(pipelineId, "Succeeded", requestedAt: DateTime.UtcNow.AddHours(-1));
        var running = await host.SeedRunAsync(pipelineId, "Running");
        var queued = await host.SeedRunAsync(pipelineId, "Queued", requestedAt: DateTime.UtcNow.AddSeconds(-5));

        //act
        var active = await host.GetAsync<List<RunSummaryDto>>("/api/runs?active=true");
        var finished = await host.GetAsync<List<RunSummaryDto>>("/api/runs?active=false");
        var all = await host.GetAsync<List<RunSummaryDto>>("/api/runs?limit=2");

        //assert
        active.Select(r => r.Id).Should().Equal(running, queued);
        finished.Select(r => r.Id).Should().Equal(newFinished, oldFinished);
        all.Should().HaveCount(2);
    }

    [Test]
    public async Task A_running_run_is_expected_to_go_through_the_stages_of_the_last_successful_one()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        await host.SeedRunAsync(
            pipelineId, "Succeeded", requestedAt: DateTime.UtcNow.AddHours(-1),
            stages: [("read", false), ("transform", false), ("load", false)]);
        var running = await host.SeedRunAsync(pipelineId, "Running", stages: [("read", false)]);

        //act
        var detail = await host.GetAsync<RunDetailDto>($"/api/runs/{running}");
        var listed = (await host.GetAsync<List<RunSummaryDto>>("/api/runs?active=true")).Single();

        //assert
        detail.Stages.Select(s => s.Name).Should().Equal("read");
        detail.ExpectedStages.Should().Equal("read", "transform", "load");
        detail.Run.StagesCompleted.Should().Be(1);
        detail.Run.StagesExpected.Should().Be(3);
        listed.StagesCompleted.Should().Be(1);
        listed.StagesExpected.Should().Be(3);
    }

    [Test]
    public async Task A_pipeline_that_never_succeeded_has_no_expected_stages()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        await host.SeedRunAsync(pipelineId, "Failed", requestedAt: DateTime.UtcNow.AddHours(-1), stages: [("read", true)]);
        var running = await host.SeedRunAsync(pipelineId, "Running");

        //act
        var detail = await host.GetAsync<RunDetailDto>($"/api/runs/{running}");

        //assert
        detail.ExpectedStages.Should().BeEmpty();
        detail.Run.StagesExpected.Should().BeNull();
    }

    [Test]
    public async Task Run_detail_carries_stage_errors_and_the_resource_samples_oldest_first()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var agentId = await host.SeedAgentAsync();
        var runId = await host.SeedRunAsync(pipelineId, "Failed", agentId, stages: [("read", false), ("load", true)]);

        await using (var context = host.NewContext())
        {
            var now = DateTime.UtcNow;
            foreach (var (offset, cpu) in new[] { (-20, 10.0), (-10, 50.0), (-30, 5.0) })
            {
                context.AgentResourceSamples.Add(new Database.Entities.AgentResourceSample
                {
                    Id = Guid.NewGuid(), RunId = runId, AgentId = agentId, SampledAt = now.AddSeconds(offset), CpuPercent = cpu, WorkingSetBytes = 1024,
                });
            }

            await context.SaveChangesAsync();
        }

        await host.ReloadAsync();

        //act
        var detail = await host.GetAsync<RunDetailDto>($"/api/runs/{runId}");

        //assert
        detail.Run.AgentName.Should().Be("agent-01");
        detail.Stages[1].ErrorCode.Should().Be("E1");
        detail.Stages[1].ErrorDescription.Should().Be("boom");
        detail.ResourceSamples.Select(s => s.CpuPercent).Should().Equal(5.0, 10.0, 50.0);
    }

    [Test]
    public async Task Every_timestamp_is_UTC_and_named_so_whatever_kind_the_database_returns()
    {
        //arrange - SQLite hands DateTimes back with an unspecified kind, which would serialise without a "Z"
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var agentId = await host.SeedAgentAsync(startedAt: DateTime.UtcNow.AddHours(-1));
        var runId = await host.SeedRunAsync(pipelineId, "Succeeded", agentId, stages: [("read", false)]);
        await using (var context = host.NewContext())
        {
            context.AgentResourceSamples.Add(new Database.Entities.AgentResourceSample
            {
                Id = Guid.NewGuid(), RunId = runId, AgentId = agentId, SampledAt = DateTime.UtcNow, CpuPercent = 1, WorkingSetBytes = 1,
            });
            await context.SaveChangesAsync();
        }

        await host.ReloadAsync();

        //act
        var run = await host.Client.GetStringAsync($"/api/runs/{runId}");
        var agents = await host.Client.GetStringAsync("/api/agents");

        //assert
        var timestamps = new List<(string Name, string Value)>();
        foreach (var json in new[] { run, agents })
        {
            CollectTimestamps(JsonDocument.Parse(json).RootElement, timestamps);
        }

        timestamps.Select(t => t.Name).Should().Contain(
            ["requestedAtUtc", "startedAtUtc", "completedAtUtc", "sampledAtUtc", "lastHeartbeatAtUtc"]);
        timestamps.Should().OnlyContain(t => t.Name.EndsWith("Utc", StringComparison.Ordinal), "every timestamp field says it is UTC");
        timestamps.Should().OnlyContain(t => t.Value.EndsWith('Z'), "and is");
        timestamps.Where(t => t.Name == "startedAtUtc").Should().OnlyContain(t => t.Value.EndsWith('Z'));
    }

    /// <summary>Every string value that parses as a date, with the name of the property it is under.</summary>
    private static void CollectTimestamps(JsonElement element, List<(string, string)> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() is { } text
                        && text.Length > 10 && char.IsDigit(text[0]) && DateTimeOffset.TryParse(text, out _))
                    {
                        found.Add((property.Name, text));
                    }
                    else
                    {
                        CollectTimestamps(property.Value, found);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectTimestamps(item, found);
                }

                break;
        }
    }

    [Test]
    public async Task An_unknown_run_is_a_404_both_as_detail_and_as_a_stream()
    {
        await using var host = await ApiTestHost.StartAsync();

        (await host.Client.GetAsync($"/api/runs/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Client.GetAsync($"/api/runs/{Guid.NewGuid()}/stream")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task An_unknown_api_path_is_a_404()
    {
        await using var host = await ApiTestHost.StartAsync();

        (await host.Client.GetAsync("/api/nothing-here")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Agents_report_their_host_details_uptime_and_the_runs_they_are_working_on()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var online = await host.SeedAgentAsync("a-online", startedAt: DateTime.UtcNow.AddHours(-2));
        await host.SeedAgentAsync("b-offline", status: "Offline", startedAt: DateTime.UtcNow.AddHours(-2));
        var running = await host.SeedRunAsync(pipelineId, "Running", online);
        await host.SeedRunAsync(pipelineId, "Succeeded", online);

        //act
        var agents = await host.GetAsync<List<AgentDto>>("/api/agents");

        //assert
        agents.Select(a => a.MachineName).Should().Equal("a-online", "b-offline");

        var first = agents[0];
        first.Status.Should().Be("Online");
        first.Info!.Value.GetProperty("osDescription").GetString().Should().Be("Ubuntu 24.04");
        first.Info.Value.GetProperty("osArchitecture").GetString().Should().Be("X64");
        first.Info.Value.GetProperty("processorCount").GetInt32().Should().Be(8);
        first.Info.Value.GetProperty("ipAddresses").EnumerateArray().Select(ip => ip.GetString())
            .Should().Equal("10.0.0.7", "2001:db8::7");
        first.Info.Value.GetProperty("somethingAddedLater").GetString().Should().Be("kept as is", "details the API does not know about pass through");
        first.Tags.Should().Equal("linux");
        first.UptimeSeconds.Should().BeInRange(2 * 3600 - 5, 2 * 3600 + 60);
        first.ActiveRuns.Select(r => r.Id).Should().Equal(running);

        agents[1].UptimeSeconds.Should().BeNull("an offline agent has no uptime");
    }
}
