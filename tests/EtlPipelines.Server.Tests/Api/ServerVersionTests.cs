using System.Net.Http.Json;
using EtlPipelines.Server.Api;
using EtlPipelines.Server.Contracts;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>The version the server shows in the WebUI.</summary>
[Category("Server")]
public class ServerVersionTests
{
    [Test]
    public void The_commit_is_kept_short_and_a_version_without_one_is_shown_as_it_is()
    {
        ServerVersion.Format("0.2.0.5+01d5a8c356a1b2c3d4e5f60718293a4b5c6d7e8f", new Version(0, 0, 0, 0)).Should().Be("0.2.0.5+01d5a8c");
        ServerVersion.Format("0.2.0", new Version(0, 0, 0, 0)).Should().Be("0.2.0");
        ServerVersion.Format("0.2.0+", null).Should().Be("0.2.0");
    }

    [Test]
    public void Without_an_informational_version_the_assembly_version_is_used_and_without_either_zero()
    {
        ServerVersion.Format(null, new Version(1, 2, 3, 4)).Should().Be("1.2.3.4");
        ServerVersion.Format(" ", null).Should().Be("0.0.0");
    }

    [Test]
    public async Task The_api_tells_the_ui_the_version_of_the_running_server()
    {
        await using var host = await ApiTestHost.StartAsync();

        var info = await host.GetAsync<ServerInfoDto>("/api/info");

        info.Version.Should().Be(ServerVersion.Current).And.NotBe("0.0.0.0");
    }
}
