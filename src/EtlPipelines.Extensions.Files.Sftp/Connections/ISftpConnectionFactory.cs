using Renci.SshNet;

namespace EtlPipelines.Extensions.Files.Sftp.Connections;

/// <summary>Opens connected SFTP clients for one named connection.</summary>
/// <remarks>
/// Shaped after <c>EtlPipelines.Extensions.Sql.Connections.IDbConnectionFactory</c> for the same reason: an
/// <see cref="ISftpClient"/> is not thread-safe and holds a socket, so the factory is what is safe to
/// register as a singleton, and each caller owns and disposes the client it is given.
/// </remarks>
public interface ISftpConnectionFactory
{
    /// <summary>The name this connection was registered under.</summary>
    string Name { get; }

    /// <summary>Opens and authenticates a client. The caller owns it and disposes it.</summary>
    ValueTask<ISftpClient> ConnectAsync(CancellationToken cancellationToken);
}
