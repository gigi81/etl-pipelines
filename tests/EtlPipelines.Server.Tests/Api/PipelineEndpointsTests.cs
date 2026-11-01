using System.Net;
using System.Net.Http.Json;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Live;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>What can be done to one pipeline: a display name, and clearing its history.</summary>
[Category("Server")]
public class PipelineEndpointsTests
{
    [Test]
    public async Task A_pipeline_can_be_given_a_display_name_while_keeping_its_own_name()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("orders-load", "Contoso.Orders");
        await host.SeedPipelineAsync("orders-archive", "Contoso.Orders");
        using var announced = host.Live.Subscribe(change => change.Kind == LiveChangeKind.Pipelines);

        //act
        var response = await SetName(host, pipelineId, "  Nightly orders  ");

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var pipelines = await host.GetAsync<List<PipelineDto>>("/api/pipelines");
        var named = pipelines.Single(p => p.Id == pipelineId);
        (named.Name, named.DisplayName).Should().Be(("orders-load", "Nightly orders"));
        pipelines.Single(p => p.Name == "orders-archive").DisplayName.Should().BeNull("only the one that was named");
        (await announced.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().NotBeEmpty("others showing the list are told");
    }

    [Test]
    public async Task Renaming_replaces_the_name_and_an_empty_one_goes_back_to_the_pipelines_own()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("orders-load");
        await SetName(host, pipelineId, "First");

        //act + assert
        await SetName(host, pipelineId, "Second");
        (await host.GetAsync<List<PipelineDto>>("/api/pipelines")).Single().DisplayName.Should().Be("Second");

        await SetName(host, pipelineId, "   ");
        (await host.GetAsync<List<PipelineDto>>("/api/pipelines")).Single().DisplayName.Should().BeNull();
        await using var context = host.NewContext();
        (await context.PipelineDisplayNames.CountAsync()).Should().Be(0, "resetting removes the row");
    }

    [Test]
    public async Task A_display_name_survives_the_package_being_updated()
    {
        //arrange - v1 is installed and its pipeline named...
        await using var host = await ApiTestHost.StartAsync();
        var v1 = await host.SeedPipelineAsync("orders-load", "Contoso.Orders", "1.0.0");
        await SetName(host, v1, "Nightly orders");

        //act - ...then v2 replaces it: a new Pipelines row with the same name
        await host.SeedPipelineAsync("orders-load", "Contoso.Orders", "2.0.0");
        await using (var context = host.NewContext())
        {
            (await context.PackageVersions.SingleAsync(v => v.Version == "1.0.0")).Status = EtlPipelines.Server.Catalog.PackageVersionStatus.Superseded;
            await context.SaveChangesAsync();
        }

        //assert
        var pipeline = (await host.GetAsync<List<PipelineDto>>("/api/pipelines")).Single();
        (pipeline.PackageVersion, pipeline.DisplayName).Should().Be(("2.0.0", "Nightly orders"));
    }

    [Test]
    public async Task A_display_name_that_is_too_long_or_for_an_unknown_pipeline_is_refused()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("orders-load");

        //act + assert
        (await SetName(host, pipelineId, new string('x', PipelineEndpointsLimit + 1))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetName(host, Guid.NewGuid(), "Name")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.GetAsync<List<PipelineDto>>("/api/pipelines")).Single().DisplayName.Should().BeNull();
    }

    [Test]
    public async Task Clearing_history_deletes_the_finished_runs_old_and_recent_and_leaves_the_one_in_progress()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var other = await host.SeedPipelineAsync("q");
        var recent = await host.SeedRunAsync(pipelineId, "Succeeded", stages: [("read", false)]);
        var longAgo = await host.SeedRunAsync(pipelineId, "Failed", requestedAt: DateTime.UtcNow.AddDays(-3), stages: [("read", true)]);
        var running = await host.SeedRunAsync(pipelineId, "Running");
        var elsewhere = await host.SeedRunAsync(other, "Succeeded");

        //act
        var response = await host.Client.DeleteAsync($"/api/pipelines/{pipelineId}/runs");

        //assert - what the API shows
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ClearedHistoryDto>(ApiTestHost.Json))!.Deleted.Should().Be(2);
        (await host.GetAsync<List<RunSummaryDto>>($"/api/runs?active=false&pipelineId={pipelineId}")).Should().BeEmpty();
        (await host.Client.GetAsync($"/api/runs/{recent}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Client.GetAsync($"/api/runs/{longAgo}")).StatusCode.Should().Be(HttpStatusCode.NotFound, "a run only the database held is hidden at once, though its row is deleted at the next sync");
        (await host.Client.GetAsync($"/api/runs/{running}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.GetAsync($"/api/runs/{elsewhere}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Clearing_history_reaches_the_database_when_it_is_synced()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        var pipelineId = await host.SeedPipelineAsync("p");
        var finished = await host.SeedRunAsync(pipelineId, "Succeeded", stages: [("read", false), ("load", false)]);
        var running = await host.SeedRunAsync(pipelineId, "Running", stages: [("read", false)]);

        //act
        await host.Client.DeleteAsync($"/api/pipelines/{pipelineId}/runs");
        await using (var context = host.NewContext())
        {
            await LiveEventWriter.WriteAsync(context, host.Live.DrainPendingEvents(), CancellationToken.None);
        }

        //assert
        await using var check = host.NewContext();
        (await check.Runs.Select(r => r.Id).ToListAsync()).Should().Equal(running);
        (await check.StageResults.Select(s => s.RunId).Distinct().ToListAsync()).Should().Equal(running);
        finished.Should().NotBeEmpty();
    }

    [Test]
    public async Task Clearing_the_history_of_an_unknown_pipeline_is_a_404()
    {
        await using var host = await ApiTestHost.StartAsync();

        (await host.Client.DeleteAsync($"/api/pipelines/{Guid.NewGuid()}/runs")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private const int PipelineEndpointsLimit = 100;

    private static Task<HttpResponseMessage> SetName(ApiTestHost host, Guid pipelineId, string? name) =>
        host.Client.PutAsJsonAsync($"/api/pipelines/{pipelineId}/display-name", new PipelineDisplayNameDto(name), ApiTestHost.Json);
}
