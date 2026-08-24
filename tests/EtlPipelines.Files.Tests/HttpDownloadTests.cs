using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Files.Tests;

public sealed class HttpDownloadTests
{
    private const string ClientName = "vendor";

    [Test]
    public async Task Downloads_one_file_to_the_named_target()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var target = fs.FileInfo.New("/work/orders.csv");
        var handler = StubHttpMessageHandler.Ok("id,customer");

        var services = NewServices(handler);
        services.AddEtlPipeline("download", b =>
            b.DownloadFromHttp(ClientName, new Uri("https://vendor.example/orders.csv"), target));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.ReadAllText("/work/orders.csv").Should().Be("id,customer");
    }

    [Test]
    public async Task Fails_with_the_status_code_and_the_url()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var target = fs.FileInfo.New("/work/orders.csv");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var services = NewServices(handler);
        services.AddEtlPipeline("download", b =>
            b.DownloadFromHttp(ClientName, new Uri("https://vendor.example/orders.csv"), target));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("503").And.Contain("orders.csv");
        fs.File.Exists("/work/orders.csv").Should().BeFalse();
    }

    [Test]
    public async Task Downloads_several_files_in_order()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var handler = new StubHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(req.RequestUri!.Segments[^1]) });

        var downloads = new[]
        {
            new HttpDownload(new Uri("https://vendor.example/a.csv"), fs.FileInfo.New("/work/a.csv")),
            new HttpDownload(new Uri("https://vendor.example/b.csv"), fs.FileInfo.New("/work/b.csv")),
        };

        var services = NewServices(handler);
        services.AddEtlPipeline("download", b => b.DownloadFromHttp(ClientName, downloads));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.ReadAllText("/work/a.csv").Should().Be("a.csv");
        fs.File.ReadAllText("/work/b.csv").Should().Be("b.csv");
    }

    [Test]
    public async Task Names_the_file_and_its_position_when_one_fails()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var handler = new StubHttpMessageHandler(req => req.RequestUri!.Segments[^1] == "b.csv"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") });

        var downloads = new[]
        {
            new HttpDownload(new Uri("https://vendor.example/a.csv"), fs.FileInfo.New("/work/a.csv")),
            new HttpDownload(new Uri("https://vendor.example/b.csv"), fs.FileInfo.New("/work/b.csv")),
        };

        var services = NewServices(handler);
        services.AddEtlPipeline("download", b => b.DownloadFromHttp(ClientName, downloads, o => o.MaxConcurrency = 1));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("file 2 of 2").And.Contain("b.csv");
    }

    [Test]
    public async Task Keeps_the_files_it_already_downloaded()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var handler = new StubHttpMessageHandler(req => req.RequestUri!.Segments[^1] == "b.csv"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") });

        var downloads = new[]
        {
            new HttpDownload(new Uri("https://vendor.example/a.csv"), fs.FileInfo.New("/work/a.csv")),
            new HttpDownload(new Uri("https://vendor.example/b.csv"), fs.FileInfo.New("/work/b.csv")),
        };

        var services = NewServices(handler);
        services.AddEtlPipeline("download", b => b.DownloadFromHttp(ClientName, downloads, o => o.MaxConcurrency = 1));

        //act
        await services.BuildServiceProvider().GetRequiredEtlPipeline("download").RunAsync(CancellationToken.None);

        //assert
        fs.File.Exists("/work/a.csv").Should().BeTrue("the file that already succeeded stays on disk");
        fs.File.Exists("/work/b.csv").Should().BeFalse();
    }

    [Test]
    public async Task Derives_a_safe_file_name_from_the_url()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var handler = StubHttpMessageHandler.Ok("x");

        var services = NewServices(handler);
        services.AddEtlPipeline("download", b => b.DownloadFromHttp(
            ClientName, fs.DirectoryInfo.New("/work"), [new Uri("https://vendor.example/reports/orders%20final.csv")]));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/orders final.csv").Should().BeTrue("the URL-encoded segment is decoded into a real file name");
    }

    [Test]
    public void Refuses_a_url_whose_last_segment_escapes_the_directory()
    {
        //arrange
        // Uri itself collapses a literal ".." in the path during parsing, so the attack that actually
        // reaches FileNameFor is a URL-encoded slash: it survives Uri unchanged and only becomes "/"
        // after UnescapeDataString, naming a path rather than a single file.
        var fs = Fixtures.NewFileSystem();

        //act
        var act = () => EtlPipeline.CreateBuilder("download")
            .DownloadFromHttp(ClientName, fs.DirectoryInfo.New("/work"), [new Uri("https://vendor.example/a%2Fb.csv")]);

        //assert
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public async Task Does_not_promote_a_partial_download()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var target = fs.FileInfo.New("/work/orders.csv");
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var services = NewServices(handler);
        services.AddEtlPipeline("download", b =>
            b.DownloadFromHttp(ClientName, new Uri("https://vendor.example/orders.csv"), target));

        //act
        await services.BuildServiceProvider().GetRequiredEtlPipeline("download").RunAsync(CancellationToken.None);

        //assert
        fs.File.Exists(target.FullName).Should().BeFalse(
            "a failed status is checked before anything is written, so the target was never created");
    }

    [Test]
    public async Task Explains_that_AddHttpClient_was_never_called()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var services = new ServiceCollection();
        services.AddEtlPipeline("download", b =>
            b.DownloadFromHttp("missing", new Uri("https://vendor.example/orders.csv"), fs.FileInfo.New("/work/orders.csv")));

        //act
        var act = async () => await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AddHttpClient*missing*");
    }

    private static ServiceCollection NewServices(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services;
    }
}
