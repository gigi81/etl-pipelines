using EtlPipelines.Server.Catalog;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>
/// The real NuGet client against a feed over plain http - which is what the deployment's own
/// feed is - browsing it, looking up versions, and pushing to it.
/// </summary>
[Category("Server")]
public class NuGetFeedClientTests
{
    [Test]
    public async Task Searching_a_plain_http_feed_finds_the_packages_with_every_version()
    {
        //arrange
        await using var feed = await FakeNuGetFeed.StartAsync();
        feed.Packages["Contoso.Orders"] = ["1.0.0", "2.0.0", "3.0.0-beta.1"];
        feed.Packages["Contoso.Billing"] = ["0.9.0"];

        //act
        var all = await new NuGetFeedClient().SearchAsync(feed.Url, null, CancellationToken.None);
        var narrowed = await new NuGetFeedClient().SearchAsync(feed.Url, "bill", CancellationToken.None);

        //assert
        all.Select(p => p.PackageId).Should().BeEquivalentTo("Contoso.Orders", "Contoso.Billing");
        all.Single(p => p.PackageId == "Contoso.Orders").Versions.Should().Equal("1.0.0", "2.0.0", "3.0.0-beta.1");
        narrowed.Select(p => p.PackageId).Should().Equal("Contoso.Billing");
    }

    [Test]
    public async Task The_latest_version_is_the_newest_stable_one()
    {
        //arrange
        await using var feed = await FakeNuGetFeed.StartAsync();
        feed.Packages["Contoso.Orders"] = ["1.0.0", "2.0.0", "3.0.0-beta.1"];

        //act
        var latest = await new NuGetFeedClient().GetLatestVersionAsync(feed.Url, "Contoso.Orders", CancellationToken.None);
        var unknown = await new NuGetFeedClient().GetLatestVersionAsync(feed.Url, "Nope", CancellationToken.None);

        //assert
        latest!.ToNormalizedString().Should().Be("2.0.0");
        unknown.Should().BeNull();
    }

    [Test]
    public async Task A_version_added_to_the_feed_is_seen_at_once_not_after_a_cache_expires()
    {
        //arrange - the version list is looked up once...
        await using var feed = await FakeNuGetFeed.StartAsync();
        feed.Packages["Contoso.Orders"] = ["1.0.0", "2.0.0"];
        var client = new NuGetFeedClient();
        (await client.GetLatestVersionAsync(feed.Url, "Contoso.Orders", CancellationToken.None))!.ToNormalizedString().Should().Be("2.0.0");

        //act - ...then someone uploads a newer one, and "update" or "install the latest" asks again
        feed.Packages["Contoso.Orders"] = ["1.0.0", "2.0.0", "3.0.0"];
        var latest = await client.GetLatestVersionAsync(feed.Url, "Contoso.Orders", CancellationToken.None);

        //assert
        latest!.ToNormalizedString().Should().Be("3.0.0");
    }
}
