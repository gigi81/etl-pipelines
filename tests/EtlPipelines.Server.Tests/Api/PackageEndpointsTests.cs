using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using EtlPipelines.AgentExecution.V1;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Contracts;
using Moq;
using NuGet.Versioning;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>The package routes the WebUI's packages page is built on.</summary>
[Category("Server")]
public class PackageEndpointsTests
{
    [Test]
    public async Task Packages_are_the_feeds_and_the_installed_ones_by_id_with_whether_an_update_is_available()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        await host.SeedPipelineAsync("orders-load", "Contoso.Orders", "1.0.0");
        await host.SeedPipelineAsync("orders-archive", "Contoso.Orders", "1.0.0");
        await host.SeedPipelineAsync("legacy", "Contoso.Legacy", "1.0.0");
        FeedOffers(host,
            new AvailableFeedPackage("Contoso.Orders", ["1.0.0", "2.0.0", "3.0.0-beta.1"]),
            new AvailableFeedPackage("Contoso.Billing", ["0.9.0"]));

        //act
        var result = await host.GetAsync<PackagesDto>("/api/packages");

        //assert
        result.FeedError.Should().BeNull();
        result.Packages.Select(p => p.Id).Should().Equal("Contoso.Billing", "Contoso.Legacy", "Contoso.Orders");

        var orders = result.Packages.Single(p => p.Id == "Contoso.Orders");
        orders.Versions.Should().Equal("3.0.0-beta.1", "2.0.0", "1.0.0");
        orders.LatestStableVersion.Should().Be("2.0.0", "a prerelease is not the latest stable version");
        orders.InstalledVersion.Should().Be("1.0.0");
        orders.UpdateAvailable.Should().BeTrue();
        orders.Pipelines.Should().Equal("orders-archive", "orders-load");

        var billing = result.Packages.Single(p => p.Id == "Contoso.Billing");
        (billing.InstalledVersion, billing.UpdateAvailable, billing.Pipelines).Should().Be((null, false, (IReadOnlyList<string>)billing.Pipelines));
        billing.Pipelines.Should().BeEmpty();

        var legacy = result.Packages.Single(p => p.Id == "Contoso.Legacy");
        legacy.Versions.Should().BeEmpty("the feed does not have it any more");
        legacy.InstalledVersion.Should().Be("1.0.0");
        legacy.UpdateAvailable.Should().BeFalse();
    }

    [Test]
    public async Task A_package_on_its_latest_stable_version_has_no_update_available()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        await host.SeedPipelineAsync("p", "Contoso.Orders", "2.0.0");
        FeedOffers(host, new AvailableFeedPackage("Contoso.Orders", ["1.0.0", "2.0.0", "3.0.0-beta.1"]));

        //act
        var orders = (await host.GetAsync<PackagesDto>("/api/packages")).Packages.Single();

        //assert
        orders.UpdateAvailable.Should().BeFalse("the newer version is only a prerelease");
    }

    [Test]
    public async Task A_search_is_passed_to_the_feed_and_narrows_the_installed_ones_too()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        await host.SeedPipelineAsync("a", "Contoso.Orders", "1.0.0");
        await host.SeedPipelineAsync("b", "Fabrikam.Reports", "1.0.0");
        host.FeedClient
            .Setup(feed => feed.SearchAsync(It.IsAny<string>(), "orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AvailableFeedPackage("Contoso.Orders", ["1.0.0"])]);

        //act
        var result = await host.GetAsync<PackagesDto>("/api/packages?search=orders");

        //assert
        result.Packages.Select(p => p.Id).Should().Equal("Contoso.Orders");
    }

    [Test]
    public async Task When_the_feed_cannot_be_reached_the_installed_packages_are_still_listed_with_the_reason()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        await host.SeedPipelineAsync("p", "Contoso.Orders", "1.0.0");
        host.FeedClient
            .Setup(feed => feed.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        //act
        var result = await host.GetAsync<PackagesDto>("/api/packages");

        //assert
        result.FeedError.Should().Contain("Connection refused");
        result.Packages.Should().ContainSingle(p => p.Id == "Contoso.Orders" && p.InstalledVersion == "1.0.0");
    }

    [Test]
    public async Task Installing_sends_the_package_to_an_agent_and_answers_with_the_pipelines_it_registered()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        var channel = Channel.CreateUnbounded<WorkItem>();
        host.Connections.Connect("agent-1", channel);

        //act
        var installing = host.Client.PostAsync("/api/packages/Contoso.Orders/install?version=1.4.0", content: null);
        var asked = await host.AnswerInstallAsync("agent-1", channel, succeeded: true, "orders-load", "orders-archive");
        var response = await installing;

        //assert
        asked.PackageId.Should().Be("Contoso.Orders");
        asked.Version.Should().Be("1.4.0");
        asked.FeedUrls.Should().Equal("http://feed.test/v3/index.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<PackageInstallResultDto>(ApiTestHost.Json))!;
        (result.PackageId, result.Version).Should().Be(("Contoso.Orders", "1.4.0"));
        result.PipelineNames.Should().BeEquivalentTo("orders-load", "orders-archive");

        FeedOffers(host, new AvailableFeedPackage("Contoso.Orders", ["1.4.0"]));
        var listed = (await host.GetAsync<PackagesDto>("/api/packages")).Packages.Single();
        (listed.InstalledVersion, listed.Pipelines).Should().Be(("1.4.0", listed.Pipelines));
        listed.Pipelines.Should().BeEquivalentTo("orders-archive", "orders-load");
    }

    [Test]
    public async Task Installing_without_a_version_takes_the_latest_stable_one_on_the_feed()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        host.FeedClient
            .Setup(feed => feed.GetLatestVersionAsync(It.IsAny<string>(), "Contoso.Orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NuGetVersion.Parse("2.5.0"));
        var channel = Channel.CreateUnbounded<WorkItem>();
        host.Connections.Connect("agent-1", channel);

        //act - no version at all
        var installing = host.Client.PostAsync("/api/packages/Contoso.Orders/install", content: null);
        var asked = await host.AnswerInstallAsync("agent-1", channel, succeeded: true, "p");
        var response = await installing;

        //assert
        asked.Version.Should().Be("2.5.0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Installing_with_no_agent_connected_is_a_409_that_says_so()
    {
        await using var host = await ApiTestHost.StartAsync();

        var response = await host.Client.PostAsync("/api/packages/Contoso.Orders/install?version=1.0.0", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Detail(response)).Should().Contain("needs an agent");
    }

    [Test]
    public async Task An_install_the_agent_reports_as_failed_is_a_502_with_the_agents_reason()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        var channel = Channel.CreateUnbounded<WorkItem>();
        host.Connections.Connect("agent-1", channel);

        //act
        var installing = host.Client.PostAsync("/api/packages/Contoso.Orders/install?version=1.0.0", content: null);
        await host.AnswerInstallAsync("agent-1", channel, succeeded: false);
        var response = await installing;

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await Detail(response)).Should().Contain("dotnet tool install failed");
    }

    [Test]
    public async Task Installing_a_package_the_feed_has_no_version_of_is_a_404()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        host.Connections.Connect("agent-1", Channel.CreateUnbounded<WorkItem>());

        //act
        var response = await host.Client.PostAsync("/api/packages/Nope/install", content: null);

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Updating_installs_the_latest_version_then_retires_the_old_one_and_tells_listeners()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        await host.SeedPipelineAsync("orders-load", "Contoso.Orders", "1.0.0");
        host.FeedClient
            .Setup(feed => feed.GetLatestVersionAsync(It.IsAny<string>(), "Contoso.Orders", It.IsAny<CancellationToken>()))
            .ReturnsAsync(NuGetVersion.Parse("2.0.0"));
        var channel = Channel.CreateUnbounded<WorkItem>();
        host.Connections.Connect("agent-1", channel);
        using var changes = host.Live.Subscribe(change => change.Kind == EtlPipelines.Server.Live.LiveChangeKind.Pipelines);

        //act
        var updating = host.Client.PostAsync("/api/packages/Contoso.Orders/update", content: null);
        var asked = await host.AnswerInstallAsync("agent-1", channel, succeeded: true, "orders-load");
        var response = await updating;

        //assert
        asked.Version.Should().Be("2.0.0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        FeedOffers(host, new AvailableFeedPackage("Contoso.Orders", ["1.0.0", "2.0.0"]));
        var listed = (await host.GetAsync<PackagesDto>("/api/packages")).Packages.Single();
        (listed.InstalledVersion, listed.UpdateAvailable).Should().Be(("2.0.0", false));
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().NotBeEmpty();
    }

    [Test]
    public async Task Updating_a_package_that_is_not_installed_is_a_409()
    {
        await using var host = await ApiTestHost.StartAsync();

        var response = await host.Client.PostAsync("/api/packages/Contoso.Orders/update", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Detail(response)).Should().Contain("not installed");
    }

    [Test]
    public async Task Uninstalling_stops_the_packages_pipelines_being_listed_and_an_unknown_package_is_a_404()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedPipelineAsync("orders-load", "Contoso.Orders", "1.0.0");
        using var changes = host.Live.Subscribe(change => change.Kind == EtlPipelines.Server.Live.LiveChangeKind.Pipelines);

        //act
        var removed = await host.Client.DeleteAsync("/api/packages/Contoso.Orders");
        var again = await host.Client.DeleteAsync("/api/packages/Contoso.Orders");

        //assert
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.GetAsync<List<PipelineDto>>("/api/pipelines")).Should().BeEmpty();
        (await changes.NextAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Should().NotBeEmpty();
    }

    [Test]
    public async Task Uploading_a_tool_package_pushes_it_to_the_feed_and_answers_with_what_it_is()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync("http://feed.test/v3/index.json");
        var package = TestPackage.Build("Contoso.Pipelines.Orders", "1.2.3");
        byte[]? pushed = null;
        string? feedUrl = null;
        string? apiKey = null;
        string? stagedAt = null;
        host.FeedClient
            .Setup(feed => feed.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((url, path, key, _) =>
            {
                feedUrl = url;
                apiKey = key;
                stagedAt = path;
                pushed = File.ReadAllBytes(path);
            })
            .Returns(Task.CompletedTask);

        //act
        var response = await host.Client.PostAsync("/api/packages/upload", new ByteArrayContent(package));

        //assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploaded = (await response.Content.ReadFromJsonAsync<UploadedPackageDto>(ApiTestHost.Json))!;
        (uploaded.Id, uploaded.Version).Should().Be(("Contoso.Pipelines.Orders", "1.2.3"));
        pushed.Should().Equal(package, "the feed got exactly the uploaded file");
        feedUrl.Should().Be("http://feed.test/v3/index.json");
        apiKey.Should().Be("any");
        File.Exists(stagedAt).Should().BeFalse("the staged copy is deleted once it has been pushed");
    }

    [Test]
    public async Task Uploading_something_that_is_not_a_tool_package_is_a_400_and_nothing_is_pushed()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();

        //act
        var notAPackage = await host.Client.PostAsync("/api/packages/upload", new ByteArrayContent("hello"u8.ToArray()));
        var notATool = await host.Client.PostAsync("/api/packages/upload", new ByteArrayContent(TestPackage.Build(dotnetTool: false)));
        var empty = await host.Client.PostAsync("/api/packages/upload", new ByteArrayContent([]));

        //assert
        notAPackage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Detail(notAPackage)).Should().Contain("not a valid NuGet package");
        notATool.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Detail(notATool)).Should().Contain("not a .NET tool package");
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.FeedClient.Verify(
            feed => feed.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Uploading_a_version_the_feed_already_has_is_a_409_and_a_feed_that_fails_is_a_502()
    {
        //arrange
        await using var host = await ApiTestHost.StartAsync();
        await host.SeedFeedAsync();
        host.FeedClient
            .SetupSequence(feed => feed.PushAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("conflict", null, HttpStatusCode.Conflict))
            .ThrowsAsync(new HttpRequestException("The feed is down"));

        //act
        var duplicate = await host.Client.PostAsync("/api/packages/upload", new ByteArrayContent(TestPackage.Build()));
        var failing = await host.Client.PostAsync("/api/packages/upload", new ByteArrayContent(TestPackage.Build()));

        //assert
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Detail(duplicate)).Should().Contain("already on the feed");
        failing.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await Detail(failing)).Should().Contain("The feed is down");
    }

    private static void FeedOffers(ApiTestHost host, params AvailableFeedPackage[] packages)
    {
        host.FeedClient
            .Setup(feed => feed.SearchAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(packages);
    }

    private static async Task<string> Detail(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
}
