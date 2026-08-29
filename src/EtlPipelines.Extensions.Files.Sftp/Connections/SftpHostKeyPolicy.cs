using EtlPipelines.Extensions.Files.Sftp.Configuration;
using Renci.SshNet.Common;

namespace EtlPipelines.Extensions.Files.Sftp.Connections;

/// <summary>
/// Decides whether to trust a host key SSH.NET presents during connect, and remembers what was
/// presented so a refusal can be reported usefully.
/// </summary>
/// <remarks>
/// Accepting any host key by default would make every SFTP pipeline in this library trivially
/// MITM-able, so the default is to refuse: with no configured fingerprint and
/// <see cref="SftpConnectionOptions.AcceptAnyHostKey"/> left <see langword="false"/>, the connection
/// is refused and <see cref="PresentedFingerprint"/> is what to show the caller, so the error can name
/// exactly what to add to configuration.
/// </remarks>
internal sealed class SftpHostKeyPolicy(SftpConnectionOptions options)
{
    /// <summary>The SHA-256 fingerprint the server presented, once a connection attempt has reached that point.</summary>
    public string? PresentedFingerprint { get; private set; }

    /// <summary>Subscribed to <see cref="Renci.SshNet.BaseClient.HostKeyReceived"/>.</summary>
    public void HostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        PresentedFingerprint = e.FingerPrintSHA256;

        e.CanTrust = options.AcceptAnyHostKey
            || options.HostKeyFingerprints.Any(configured => Matches(configured, e.FingerPrintSHA256));
    }

    /// <summary>
    /// Compares a configured fingerprint against what was presented, accepting both the bare base64
    /// SSH.NET reports and the <c>SHA256:</c>-prefixed form <c>ssh-keygen -lf</c> prints.
    /// </summary>
    private static bool Matches(string configured, string presented)
    {
        var normalized = configured.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? configured["SHA256:".Length..]
            : configured;

        return string.Equals(normalized.TrimEnd('='), presented.TrimEnd('='), StringComparison.Ordinal);
    }
}
