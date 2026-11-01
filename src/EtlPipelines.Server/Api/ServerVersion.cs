using System.Reflection;

namespace EtlPipelines.Server.Api;

/// <summary>The version this server reports, shown in the WebUI.</summary>
/// <remarks>
/// Not the assembly version: the build (Nerdbank.GitVersioning) keeps that to the major number only, so
/// version 0.2.0 has assembly version 0.0.0.0. The informational version has the real one, followed by the
/// commit it was built from. (The agent reports its own the same way, in <c>EtlPipelines.Agent.AgentVersion</c>.)
/// </remarks>
public static class ServerVersion
{
    private const int CommitLength = 7;

    /// <summary>The version of the running server: <c>0.2.0.5+1a2b3c4</c>.</summary>
    public static string Current { get; } = Describe(typeof(ServerVersion).Assembly);

    /// <summary>The version <paramref name="assembly"/> was built as.</summary>
    public static string Describe(Assembly assembly) =>
        Format(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, assembly.GetName().Version);

    /// <summary>
    /// <paramref name="informationalVersion"/> with its commit shortened; the assembly version when there is
    /// none, and <c>0.0.0</c> when there is neither.
    /// </summary>
    public static string Format(string? informationalVersion, Version? assemblyVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return assemblyVersion?.ToString() ?? "0.0.0";
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return informationalVersion.Trim();
        }

        var version = informationalVersion[..plus];
        var commit = informationalVersion[(plus + 1)..];
        return commit.Length == 0 ? version : $"{version}+{commit[..Math.Min(commit.Length, CommitLength)]}";
    }
}
