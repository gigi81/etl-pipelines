using System.IO.Abstractions.TestingHelpers;
using EtlPipelines.Core;
using EtlPipelines.Files.Sftp;
using EtlPipelines.Files.Sftp.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Files.Servers.Tests;

[Category("Docker")]
[ClassDataSource<SftpFixture>(Shared = SharedType.PerAssembly)]
public sealed class SftpServerTests(SftpFixture fixture)
{
    private const string ConnectionName = "vendor";

    [Test]
    public async Task Uploads_and_downloads_a_file_against_a_real_server()
    {
        //arrange
        var fs = new MockFileSystem();
        fs.AddFile("/local/orders.csv", new MockFileData("id,customer\n1,acme"));

        var services = NewServices(o => o.AcceptAnyHostKey = true);
        services.AddEtlPipeline("upload", b =>
            b.UploadToSftp(ConnectionName, fs.FileInfo.New("/local/orders.csv"), $"/{SftpFixture.Directory}/orders.csv"));
        services.AddEtlPipeline("download", b =>
            b.DownloadFromSftp(ConnectionName, $"/{SftpFixture.Directory}/orders.csv", fs.FileInfo.New("/local/roundtrip.csv")));

        var provider = services.BuildServiceProvider();

        //act
        var uploaded = await provider.GetRequiredEtlPipeline("upload").RunAsync(CancellationToken.None);
        var downloaded = await provider.GetRequiredEtlPipeline("download").RunAsync(CancellationToken.None);

        //assert
        uploaded.IsError.Should().BeFalse(uploaded.IsError ? uploaded.FirstError.Description : null);
        downloaded.IsError.Should().BeFalse(downloaded.IsError ? downloaded.FirstError.Description : null);
        fs.File.ReadAllText("/local/roundtrip.csv").Should().Be("id,customer\n1,acme");
    }

    [Test]
    public async Task Lists_and_filters_a_real_remote_directory()
    {
        //arrange
        var fs = new MockFileSystem();
        fs.AddFile("/local/a.csv", new MockFileData("a"));
        fs.AddFile("/local/b.csv", new MockFileData("b"));
        fs.AddFile("/local/c.txt", new MockFileData("c"));

        var services = NewServices(o => o.AcceptAnyHostKey = true);
        services.AddEtlPipeline("upload", b => b.UploadToSftp(
            ConnectionName, fs.DirectoryInfo.New("/local"), "*", $"/{SftpFixture.Directory}/listing"));
        services.AddEtlPipeline("download", b => b.DownloadFromSftp(
            ConnectionName, $"/{SftpFixture.Directory}/listing", "*.csv", fs.DirectoryInfo.New("/local/out")));

        var provider = services.BuildServiceProvider();

        //act
        var uploaded = await provider.GetRequiredEtlPipeline("upload").RunAsync(CancellationToken.None);
        var downloaded = await provider.GetRequiredEtlPipeline("download").RunAsync(CancellationToken.None);

        //assert
        uploaded.IsError.Should().BeFalse(uploaded.IsError ? uploaded.FirstError.Description : null);
        downloaded.IsError.Should().BeFalse(downloaded.IsError ? downloaded.FirstError.Description : null);
        fs.File.Exists("/local/out/a.csv").Should().BeTrue();
        fs.File.Exists("/local/out/b.csv").Should().BeTrue();
        fs.File.Exists("/local/out/c.txt").Should().BeFalse();
    }

    [Test]
    public async Task Deletes_the_remote_file_after_a_successful_download()
    {
        //arrange
        var fs = new MockFileSystem();
        fs.AddFile("/local/gone.csv", new MockFileData("x"));

        var services = NewServices(o => o.AcceptAnyHostKey = true);
        services.AddEtlPipeline("upload", b =>
            b.UploadToSftp(ConnectionName, fs.FileInfo.New("/local/gone.csv"), $"/{SftpFixture.Directory}/gone.csv"));
        services.AddEtlPipeline("download", b => b.DownloadFromSftp(
            ConnectionName, $"/{SftpFixture.Directory}/gone.csv", fs.FileInfo.New("/local/gone-copy.csv"),
            o => o.DeleteRemoteAfterDownload = true));
        services.AddEtlPipeline("verify", b => b.DownloadFromSftp(
            ConnectionName, $"/{SftpFixture.Directory}/gone.csv", fs.FileInfo.New("/local/should-not-exist.csv")));

        var provider = services.BuildServiceProvider();

        //act
        await provider.GetRequiredEtlPipeline("upload").RunAsync(CancellationToken.None);
        var downloaded = await provider.GetRequiredEtlPipeline("download").RunAsync(CancellationToken.None);
        var verify = await provider.GetRequiredEtlPipeline("verify").RunAsync(CancellationToken.None);

        //assert
        downloaded.IsError.Should().BeFalse(downloaded.IsError ? downloaded.FirstError.Description : null);
        verify.IsError.Should().BeTrue("the remote file was deleted once the download completed, so a second download must fail");
    }

    [Test]
    public async Task Reports_the_fingerprint_the_server_presented_when_no_host_key_is_configured()
    {
        //arrange
        var expected = await fixture.Ed25519FingerprintAsync();

        var fs = new MockFileSystem();
        var services = NewServices();
        services.AddEtlPipeline("download", b => b.DownloadFromSftp(
            ConnectionName, $"/{SftpFixture.Directory}/missing.csv", fs.FileInfo.New("/local/missing.csv")));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should()
            .Contain(expected, "the error names the exact fingerprint to paste into configuration")
            .And.Contain("HostKeyFingerprints")
            .And.Contain("AcceptAnyHostKey");
    }

    [Test]
    public async Task Connects_when_the_configured_fingerprint_matches()
    {
        //arrange
        var expected = await fixture.Ed25519FingerprintAsync();

        var fs = new MockFileSystem();
        fs.AddFile("/local/a.csv", new MockFileData("a"));

        var services = NewServices(o => o.HostKeyFingerprints.Add(expected));
        services.AddEtlPipeline("upload", b =>
            b.UploadToSftp(ConnectionName, fs.FileInfo.New("/local/a.csv"), $"/{SftpFixture.Directory}/trusted.csv"));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("upload")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
    }

    [Test]
    public async Task Refuses_to_connect_when_the_configured_fingerprint_does_not_match()
    {
        //arrange
        var fs = new MockFileSystem();
        fs.AddFile("/local/a.csv", new MockFileData("a"));

        var services = NewServices(o => o.HostKeyFingerprints.Add("not-the-right-fingerprint"));
        services.AddEtlPipeline("upload", b =>
            b.UploadToSftp(ConnectionName, fs.FileInfo.New("/local/a.csv"), $"/{SftpFixture.Directory}/untrusted.csv"));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("upload")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
    }

    [Test]
    public async Task Fails_with_an_authentication_error_for_a_bad_password()
    {
        //arrange
        var fs = new MockFileSystem();
        var services = new ServiceCollection();
        services.AddSftpConnection(ConnectionName, fixture.Host, SftpFixture.UserName, o =>
        {
            o.Port = fixture.Port;
            o.Password = "definitely-wrong";
            o.AcceptAnyHostKey = true;
        });

        services.AddEtlPipeline("download", b => b.DownloadFromSftp(
            ConnectionName, $"/{SftpFixture.Directory}/x.csv", fs.FileInfo.New("/local/x.csv")));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("Permission denied");
    }

    [Test]
    public async Task Fails_for_a_missing_remote_directory()
    {
        //arrange
        var fs = new MockFileSystem();
        var services = NewServices(o => o.AcceptAnyHostKey = true);
        services.AddEtlPipeline("download", b => b.DownloadFromSftp(
            ConnectionName, $"/{SftpFixture.Directory}/does-not-exist", "*.csv", fs.DirectoryInfo.New("/local/out")));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("does-not-exist");
    }

    private ServiceCollection NewServices(Action<SftpConnectionOptions>? configure = null)
    {
        var services = new ServiceCollection();

        services.AddSftpConnection(ConnectionName, fixture.Host, SftpFixture.UserName, o =>
        {
            o.Port = fixture.Port;
            o.Password = SftpFixture.Password;
            configure?.Invoke(o);
        });

        return services;
    }
}
