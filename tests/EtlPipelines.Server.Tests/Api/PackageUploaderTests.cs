using System.Net;
using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Database.Entities;
using Microsoft.Extensions.Configuration;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>
/// <see cref="PackageUploader"/> with the real NuGet client against a fake feed that speaks the push
/// protocol: what is sent, with which key, and how the feed's own refusals come out.
/// </summary>
[Category("Server")]
public class PackageUploaderTests
{
    [Test]
    public async Task The_package_and_the_api_key_reach_the_feed_over_the_real_push_protocol()
    {
        //arrange
        await using var feed = await FakeNuGetFeed.StartAsync();
        var uploader = await CreateAsync(feed.Url, apiKey: "s3cret");
        var package = TestPackage.Build("Contoso.Pipelines.Orders", "1.2.3");

        //act
        var uploaded = await uploader.UploadAsync(new MemoryStream(package), CancellationToken.None);

        //assert
        (uploaded.Id, uploaded.Version).Should().Be(("Contoso.Pipelines.Orders", "1.2.3"));
        var push = feed.Pushes.Should().ContainSingle().Which;
        push.ApiKey.Should().Be("s3cret");
        ContainsSequence(push.Body, package).Should().BeTrue("the .nupkg is in the multipart body, byte for byte");
    }

    [Test]
    public async Task A_version_the_feed_refuses_as_already_there_is_reported_as_such()
    {
        //arrange
        await using var feed = await FakeNuGetFeed.StartAsync();
        feed.PushStatus = HttpStatusCode.Conflict;
        var uploader = await CreateAsync(feed.Url);

        //act
        var act = () => uploader.UploadAsync(new MemoryStream(TestPackage.Build()), CancellationToken.None);

        //assert
        var exception = await act.Should().ThrowAsync<PackageUploadException>();
        exception.Which.Error.Should().Be(PackageUploadError.AlreadyExists);
        exception.Which.Message.Should().Contain("Contoso.Pipelines.Orders 1.2.3 is already on the feed");
    }

    [Test]
    public async Task A_feed_that_fails_or_is_not_there_is_reported_as_unavailable()
    {
        //arrange
        await using var failing = await FakeNuGetFeed.StartAsync();
        failing.PushStatus = HttpStatusCode.InternalServerError;
        var uploader = await CreateAsync(failing.Url);
        var gone = await CreateAsync("http://127.0.0.1:1/v3/index.json");

        //act
        var failed = () => uploader.UploadAsync(new MemoryStream(TestPackage.Build()), CancellationToken.None);
        var unreachable = () => gone.UploadAsync(new MemoryStream(TestPackage.Build()), CancellationToken.None);

        //assert
        (await failed.Should().ThrowAsync<PackageUploadException>()).Which.Error.Should().Be(PackageUploadError.FeedUnavailable);
        (await unreachable.Should().ThrowAsync<PackageUploadException>()).Which.Error.Should().Be(PackageUploadError.FeedUnavailable);
    }

    [Test]
    public async Task Nothing_is_sent_for_a_file_that_is_not_a_tool_package()
    {
        //arrange
        await using var feed = await FakeNuGetFeed.StartAsync();
        var uploader = await CreateAsync(feed.Url);

        //act
        var act = () => uploader.UploadAsync(new MemoryStream(TestPackage.Build(dotnetTool: false)), CancellationToken.None);

        //assert
        (await act.Should().ThrowAsync<PackageUploadException>()).Which.Error.Should().Be(PackageUploadError.Invalid);
        feed.Pushes.Should().BeEmpty();
    }

    private static async Task<PackageUploader> CreateAsync(string feedUrl, string? apiKey = null)
    {
        var context = SqliteServerDbContext.Create();
        context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = feedUrl, Ordinal = 0 });
        await context.SaveChangesAsync();

        var settings = new Dictionary<string, string?>();
        if (apiKey is not null)
        {
            settings[PackageUploader.ApiKeySetting] = apiKey;
        }

        return new PackageUploader(
            new PackageCatalogService(context, new NuGetFeedClient()),
            new NuGetFeedClient(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
