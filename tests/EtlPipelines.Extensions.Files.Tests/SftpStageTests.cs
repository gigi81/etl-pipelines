using System.IO.Abstractions.TestingHelpers;
using EtlPipelines.Extensions.Files.Sftp;
using EtlPipelines.Extensions.Files.Sftp.Connections;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace EtlPipelines.Extensions.Files.Tests;

public sealed class SftpStageTests
{
    private const string ConnectionName = "vendor";

    [Test]
    public async Task Downloads_one_remote_file()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var client = new Mock<ISftpClient>();
        client.Setup(c => c.DownloadFileAsync("/out/orders.csv", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<string, Stream, CancellationToken>(async (_, stream, ct) =>
            {
                var bytes = "id,customer"u8.ToArray();
                await stream.WriteAsync(bytes, ct);
            });

        var services = NewServices(client.Object);
        services.AddEtlPipeline("download", b =>
            b.DownloadFromSftp(ConnectionName, "/out/orders.csv", fs.FileInfo.New("/work/orders.csv")));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.ReadAllText("/work/orders.csv").Should().Be("id,customer");
        client.Verify(c => c.Dispose(), Times.Once, "the stage owns the client it connected and disposes it");
    }

    [Test]
    public async Task Downloads_every_remote_file_matching_the_pattern_and_skips_the_rest()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var client = new Mock<ISftpClient>();

        client.Setup(c => c.ListDirectoryAsync("/out", It.IsAny<CancellationToken>()))
            .Returns(AsyncFiles(
                FakeDirectory("."),
                FakeDirectory(".."),
                FakeFile("a.csv"),
                FakeFile("b.csv"),
                FakeFile("c.txt")));

        client.Setup(c => c.DownloadFileAsync(It.IsRegex(@"\.csv$"), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<string, Stream, CancellationToken>(async (path, stream, ct) =>
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(path);
                await stream.WriteAsync(bytes, ct);
            });

        var services = NewServices(client.Object);
        services.AddEtlPipeline("download", b =>
            b.DownloadFromSftp(ConnectionName, "/out", "*.csv", fs.DirectoryInfo.New("/work")));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/a.csv").Should().BeTrue();
        fs.File.Exists("/work/b.csv").Should().BeTrue();
        fs.File.Exists("/work/c.txt").Should().BeFalse();
        client.Verify(c => c.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task Deletes_the_remote_file_only_after_the_local_one_is_promoted()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var client = new Mock<ISftpClient>();
        client.Setup(c => c.DownloadFileAsync("/out/orders.csv", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<string, Stream, CancellationToken>(async (_, stream, ct) => await stream.WriteAsync("x"u8.ToArray(), ct));
        client.Setup(c => c.DeleteFileAsync("/out/orders.csv", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var services = NewServices(client.Object);
        services.AddEtlPipeline("download", b => b.DownloadFromSftp(
            ConnectionName, "/out/orders.csv", fs.FileInfo.New("/work/orders.csv"),
            o => o.DeleteRemoteAfterDownload = true));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        client.Verify(c => c.DeleteFileAsync("/out/orders.csv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Does_not_delete_the_remote_file_when_the_local_write_fails()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var client = new Mock<ISftpClient>();
        client.Setup(c => c.DownloadFileAsync("/out/orders.csv", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SftpPathNotFoundException("No such file."));

        var services = NewServices(client.Object);
        services.AddEtlPipeline("download", b => b.DownloadFromSftp(
            ConnectionName, "/out/orders.csv", fs.FileInfo.New("/work/orders.csv"),
            o => o.DeleteRemoteAfterDownload = true));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        client.Verify(c => c.DeleteFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Uploads_a_local_selection()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/a.csv", new MockFileData("a"));
        fs.AddFile("/work/b.csv", new MockFileData("b"));

        var client = new Mock<ISftpClient>();
        client.Setup(c => c.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.UploadFileAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), true, It.IsAny<IProgress<UploadFileProgressReport>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = NewServices(client.Object);
        services.AddEtlPipeline("upload", b => b.UploadToSftp(ConnectionName, fs.DirectoryInfo.New("/work"), "*.csv", "/in"));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("upload")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        client.Verify(c => c.UploadFileAsync(
            It.IsAny<Stream>(), "/in/a.csv", true, It.IsAny<IProgress<UploadFileProgressReport>>(), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.UploadFileAsync(
            It.IsAny<Stream>(), "/in/b.csv", true, It.IsAny<IProgress<UploadFileProgressReport>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Maps_SftpPathNotFoundException_to_an_error_naming_the_path()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var client = new Mock<ISftpClient>();
        client.Setup(c => c.ListDirectoryAsync("/missing", It.IsAny<CancellationToken>()))
            .Returns(ThrowingListing());

        var services = NewServices(client.Object);
        services.AddEtlPipeline("download", b =>
            b.DownloadFromSftp(ConnectionName, "/missing", "*.csv", fs.DirectoryInfo.New("/work")));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("/missing");
    }

    [Test]
    public async Task Maps_SshAuthenticationException_to_an_error()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ISftpConnectionFactory>(ConnectionName, new ThrowingConnectionFactory(
            ConnectionName, new SshAuthenticationException("Permission denied (password).")));

        services.AddEtlPipeline("download", b =>
            b.DownloadFromSftp(ConnectionName, "/out/orders.csv", fs.FileInfo.New("/work/orders.csv")));

        //act
        var result = await services.BuildServiceProvider().GetRequiredEtlPipeline("download")
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("Permission denied");
    }

    [Test]
    public async Task Disposes_the_client_even_when_a_file_fails()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var client = new Mock<ISftpClient>();
        client.Setup(c => c.DownloadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SftpPathNotFoundException("No such file."));

        var services = NewServices(client.Object);
        services.AddEtlPipeline("download", b =>
            b.DownloadFromSftp(ConnectionName, "/out/orders.csv", fs.FileInfo.New("/work/orders.csv")));

        //act
        await services.BuildServiceProvider().GetRequiredEtlPipeline("download").RunAsync(CancellationToken.None);

        //assert
        client.Verify(c => c.Dispose(), Times.Once);
    }

    private static async IAsyncEnumerable<ISftpFile> AsyncFiles(params ISftpFile[] files)
    {
        foreach (var file in files)
        {
            yield return file;
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ISftpFile> ThrowingListing()
    {
        await Task.CompletedTask;
        throw new SftpPathNotFoundException("No such directory.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static ISftpFile FakeFile(string name)
    {
        var file = new Mock<ISftpFile>();
        file.SetupGet(f => f.Name).Returns(name);
        file.SetupGet(f => f.FullName).Returns($"/out/{name}");
        file.SetupGet(f => f.IsRegularFile).Returns(true);
        file.SetupGet(f => f.LastWriteTime).Returns(DateTime.UtcNow);
        file.SetupGet(f => f.Length).Returns(1);
        return file.Object;
    }

    private static ISftpFile FakeDirectory(string name)
    {
        var file = new Mock<ISftpFile>();
        file.SetupGet(f => f.Name).Returns(name);
        file.SetupGet(f => f.FullName).Returns($"/out/{name}");
        file.SetupGet(f => f.IsRegularFile).Returns(false);
        file.SetupGet(f => f.IsDirectory).Returns(true);
        return file.Object;
    }

    private static ServiceCollection NewServices(ISftpClient client)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ISftpConnectionFactory>(ConnectionName, new FakeSftpConnectionFactory(ConnectionName, client));
        return services;
    }

    /// <summary>Hands back an already-built client instead of really connecting.</summary>
    private sealed class FakeSftpConnectionFactory(string name, ISftpClient client) : ISftpConnectionFactory
    {
        public string Name => name;

        public ValueTask<ISftpClient> ConnectAsync(CancellationToken cancellationToken) => ValueTask.FromResult(client);
    }

    /// <summary>Fails to connect, the way a real factory does when authentication is rejected.</summary>
    private sealed class ThrowingConnectionFactory(string name, Exception exception) : ISftpConnectionFactory
    {
        public string Name => name;

        public ValueTask<ISftpClient> ConnectAsync(CancellationToken cancellationToken) => throw exception;
    }
}
