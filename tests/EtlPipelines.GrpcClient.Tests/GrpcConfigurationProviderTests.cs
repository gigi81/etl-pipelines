using EtlPipelines.PipelineExecution.V1;
using Grpc.Core;
using Moq;

namespace EtlPipelines.GrpcClient.Tests;

/// <summary>
/// Fast test for <see cref="GrpcConfigurationProvider"/> against a mocked
/// <see cref="PipelineExecutionService.PipelineExecutionServiceClient"/> - SERVER.md Phase 6's own
/// test plan for it, made possible by <see cref="GrpcConfigurationSource"/> taking an
/// already-built client rather than a server url (see that type's own remarks).
/// </summary>
[Category("GrpcClient")]
public class GrpcConfigurationProviderTests
{
    [Test]
    public async Task Load_calls_GetConfiguration_with_the_session_id_and_materializes_every_entry()
    {
        //arrange
        var client = new Mock<PipelineExecutionService.PipelineExecutionServiceClient>();
        var response = new GetConfigurationResponse
        {
            Entries =
            {
                ["ConnectionStrings:sales"] = "Host=db;Database=sales",
                ["Sftp:vendor:Host"] = "sftp.example.com",
            },
        };

        client
            .Setup(c => c.GetConfiguration(
                It.Is<GetConfigurationRequest>(r => r.SessionId == "session-1"),
                It.IsAny<Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(response);

        var provider = new GrpcConfigurationProvider(client.Object, "session-1");

        //act
        provider.Load();

        //assert
        provider.TryGet("ConnectionStrings:sales", out var connectionString).Should().BeTrue();
        connectionString.Should().Be("Host=db;Database=sales");

        provider.TryGet("Sftp:vendor:Host", out var host).Should().BeTrue();
        host.Should().Be("sftp.example.com");

        client.VerifyAll();
    }

    [Test]
    public async Task Load_with_no_entries_leaves_the_provider_empty()
    {
        //arrange
        var client = new Mock<PipelineExecutionService.PipelineExecutionServiceClient>();
        client
            .Setup(c => c.GetConfiguration(
                It.IsAny<GetConfigurationRequest>(), It.IsAny<Metadata>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(new GetConfigurationResponse());

        var provider = new GrpcConfigurationProvider(client.Object, "session-1");

        //act
        provider.Load();

        //assert
        provider.TryGet("ConnectionStrings:sales", out _).Should().BeFalse();
    }
}
