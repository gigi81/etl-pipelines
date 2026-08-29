using System.IO.Abstractions;
using Renci.SshNet;

namespace EtlPipelines.Extensions.Files.Sftp.Configuration;

/// <summary>Settings for one named SFTP connection.</summary>
public sealed class SftpConnectionOptions
{
    /// <summary>The server host name or address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The server port. Defaults to 22.</summary>
    public int Port { get; set; } = 22;

    /// <summary>The user name to authenticate as.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>A password to authenticate with. Prefer a private key; keep this out of committed configuration.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// The path to a private key file, bindable from configuration. <see cref="PrivateKey"/> wins
    /// when both are set.
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>A private key file, set in code. Wins over <see cref="PrivateKeyPath"/>.</summary>
    public IFileInfo? PrivateKey { get; set; }

    /// <summary>The private key's passphrase, if it has one.</summary>
    public string? PrivateKeyPassPhrase { get; set; }

    /// <summary>
    /// SHA-256 host key fingerprints this connection accepts, either the bare base64 SSH.NET reports
    /// or the <c>SHA256:</c>-prefixed form <c>ssh-keygen -lf</c> prints - both are matched.
    /// </summary>
    public IList<string> HostKeyFingerprints { get; } = [];

    /// <summary>
    /// Skips host key verification entirely. Defaults to <see langword="false"/>, under which a
    /// connection with no matching fingerprint in <see cref="HostKeyFingerprints"/> is refused rather
    /// than silently trusted. Set this only when you understand you are trusting the network.
    /// </summary>
    public bool AcceptAnyHostKey { get; set; }

    /// <summary>How long the initial connection may take. Defaults to 30 seconds.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a single SFTP operation may take. Defaults to 5 minutes.</summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The remote directory relative paths are resolved against. Defaults to <see langword="null"/>, the server's own default.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Escape hatch applied last - proxies, key-exchange preferences, keyboard-interactive authentication.</summary>
    public Action<ConnectionInfo>? Configure { get; set; }
}
