using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace EtlPipelines.Files.Servers.Tests;

/// <summary>
/// One <c>atmoz/sftp</c> container, shared for the whole test project the same way
/// <c>EtlPipelines.Sql.Databases.Tests.DatabaseFixture</c> shares a database.
/// </summary>
/// <remarks>
/// Created with a fixed user (<see cref="UserName"/>/<see cref="Password"/>) and one writable
/// directory (<see cref="Directory"/>). SSH.NET negotiates its preferred host key algorithm when the
/// server offers more than one - confirmed to be ed25519 over the RSA key atmoz/sftp also generates -
/// so tests reading the presented fingerprint back out of the container ask for that key specifically.
/// </remarks>
public sealed class SftpFixture : IAsyncInitializer, IAsyncDisposable
{
    public const string UserName = "etl";
    public const string Password = "etlpass";
    public const string Directory = "inbox";

    private readonly Lazy<IContainer> _container = new(() => new ContainerBuilder(ContainerImages.Sftp)
        .WithCommand($"{UserName}:{Password}:::{Directory}")
        .WithPortBinding(22, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(22))
        .Build());

    public IContainer Container => _container.Value;

    public string Host => Container.Hostname;

    public ushort Port => Container.GetMappedPublicPort(22);

    public Task InitializeAsync() => Container.StartAsync();

    public ValueTask DisposeAsync() => Container.DisposeAsync();

    /// <summary>The SHA-256 fingerprint of the server's ed25519 host key, read directly out of the container.</summary>
    public async Task<string> Ed25519FingerprintAsync()
    {
        var result = await Container.ExecAsync(
            ["ssh-keygen", "-lf", "/etc/ssh/ssh_host_ed25519_key.pub"], CancellationToken.None);

        // "256 SHA256:yj68R4HDZf/iRLGwsOg2rbfuPU1Sohy1LVuD6qgudZs root@... (ED25519)"
        var parts = result.Stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts[1];
    }
}
