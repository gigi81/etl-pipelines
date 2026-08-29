using EtlPipelines.Extensions.Files.Sftp.Configuration;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace EtlPipelines.Extensions.Files.Sftp.Connections;

/// <summary>Opens SFTP clients for one named connection, from configuration read at connect time.</summary>
public sealed class SftpConnectionFactory : ISftpConnectionFactory
{
    private readonly Func<SftpConnectionOptions> _options;

    /// <summary>Opens connections for <paramref name="name"/>, resolving settings with <paramref name="options"/> at connect time.</summary>
    public SftpConnectionFactory(string name, Func<SftpConnectionOptions> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        Name = name;
        _options = options;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ISftpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var options = _options();

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new InvalidOperationException(
                $"The SFTP connection '{Name}' has no host configured. Set it under Sftp:{Name}:Host, " +
                $"or pass it to services.AddSftpConnection(\"{Name}\", host, userName, ...).");
        }

        var authentications = new List<AuthenticationMethod>();

        var keyFile = ResolvePrivateKey(options);

        if (keyFile is not null)
        {
            authentications.Add(new PrivateKeyAuthenticationMethod(options.UserName, [keyFile]));
        }

        if (!string.IsNullOrEmpty(options.Password) || authentications.Count == 0)
        {
            authentications.Add(new PasswordAuthenticationMethod(options.UserName, options.Password ?? string.Empty));
        }

        var connectionInfo = new ConnectionInfo(options.Host, options.Port, options.UserName, [.. authentications])
        {
            Timeout = options.ConnectionTimeout,
        };

        options.Configure?.Invoke(connectionInfo);

        var client = new SftpClient(connectionInfo)
        {
            OperationTimeout = options.OperationTimeout,
        };

        var hostKeyPolicy = new SftpHostKeyPolicy(options);
        client.HostKeyReceived += hostKeyPolicy.HostKeyReceived;

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (hostKeyPolicy.PresentedFingerprint is { } fingerprint
            && !options.AcceptAnyHostKey && options.HostKeyFingerprints.Count == 0)
        {
            client.Dispose();

            throw new InvalidOperationException(
                $"The SFTP connection '{Name}' has no expected host key. {options.Host}:{options.Port} " +
                $"presented 'SHA256:{fingerprint}'. If that is the right server, add it under " +
                $"Sftp:{Name}:HostKeyFingerprints:0. To skip the check, set Sftp:{Name}:AcceptAnyHostKey to true.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            await client.ChangeDirectoryAsync(options.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        }

        return client;
    }

    private static PrivateKeyFile? ResolvePrivateKey(SftpConnectionOptions options)
    {
        if (options.PrivateKey is { } file)
        {
            file.Refresh();

            using var stream = file.OpenRead();

            return string.IsNullOrEmpty(options.PrivateKeyPassPhrase)
                ? new PrivateKeyFile(stream)
                : new PrivateKeyFile(stream, options.PrivateKeyPassPhrase);
        }

        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            return string.IsNullOrEmpty(options.PrivateKeyPassPhrase)
                ? new PrivateKeyFile(options.PrivateKeyPath)
                : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassPhrase);
        }

        return null;
    }
}
