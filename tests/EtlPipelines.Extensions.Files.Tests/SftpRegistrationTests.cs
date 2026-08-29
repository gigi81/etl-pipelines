using EtlPipelines.Extensions.Files.Sftp;
using EtlPipelines.Extensions.Files.Sftp.Configuration;
using EtlPipelines.Extensions.Files.Sftp.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Extensions.Files.Tests;

public sealed class SftpRegistrationTests
{
    [Test]
    public void Explains_which_registration_call_is_missing()
    {
        //arrange
        var provider = new ServiceCollection().BuildServiceProvider();

        //act
        var act = () => provider.GetRequiredSftpConnectionFactory("vendor");

        //assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*vendor*")
            .WithMessage("*AddSftpConnection*")
            .WithMessage("*Sftp:vendor*");
    }

    [Test]
    public async Task Explains_which_configuration_key_is_missing()
    {
        //arrange
        // No IConfiguration registered at all, and the literal-value overload was not used either -
        // binding has nothing to read from, so Host stays empty and the failure is immediate, without
        // ever touching the network.
        var services = new ServiceCollection();
        services.AddSftpConnection("vendor");

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredSftpConnectionFactory("vendor");

        //act
        var act = async () => await factory.ConnectAsync(CancellationToken.None);

        //assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*vendor*")
            .WithMessage("*Host*")
            .WithMessage("*Sftp:vendor*");
    }

    [Test]
    public void Binds_host_port_and_user_name_from_the_Sftp_configuration_section()
    {
        //arrange
        // The same binding call AddSftpConnection makes at connect time, exercised directly so this
        // test needs no network - only that SftpConnectionOptions is a shape Bind actually populates.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sftp:vendor:Host"] = "sftp.example.com",
                ["Sftp:vendor:Port"] = "2222",
                ["Sftp:vendor:UserName"] = "etl",
                ["Sftp:vendor:HostKeyFingerprints:0"] = "SHA256:abc123",
            })
            .Build();

        var options = new SftpConnectionOptions();

        //act
        configuration.GetSection("Sftp:vendor").Bind(options);

        //assert
        options.Host.Should().Be("sftp.example.com");
        options.Port.Should().Be(2222);
        options.UserName.Should().Be("etl");
        options.HostKeyFingerprints.Should().ContainSingle().Which.Should().Be("SHA256:abc123");
    }

    [Test]
    public void Registers_a_literal_connection_without_needing_IConfiguration_at_all()
    {
        //arrange
        var services = new ServiceCollection();

        //act
        var act = () => services.AddSftpConnection("vendor", "sftp.example.com", "etl");

        //assert
        act.Should().NotThrow("the literal overload takes everything it needs as arguments");
        services.Should().Contain(d => d.ServiceKey as string == "vendor" && d.ServiceType == typeof(ISftpConnectionFactory));
    }
}
