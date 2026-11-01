using System.Reflection;

namespace EtlPipelines.Agent;

/// <summary>The version this agent reports when it registers, shown on the WebUI's agents page.</summary>
/// <remarks>
/// Not the assembly version: the build (Nerdbank.GitVersioning) keeps that to the major number only, so
/// version 0.2.0 has assembly version 0.0.0.0. The informational version has the real one, followed by the
/// commit it was built from.
/// </remarks>
public static class AgentVersion
{
    /// <summary>How many characters of the commit are kept.</summary>
    private const int CommitLength = 7;

    /// <summary>The version of the running agent: <c>0.2.0+1a2b3c4</c>.</summary>
    public static string Current { get; } = Describe(typeof(AgentVersion).Assembly);

    /// <summary>The version <paramref name="assembly"/> was built as.</summary>
    public static string Describe(Assembly assembly) =>
        Format(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, assembly.GetName().Version);

    /// <summary>
    /// <paramref name="informationalVersion"/> with its commit shortened (<c>0.2.0+1a2b3c4d5e...</c> becomes
    /// <c>0.2.0+1a2b3c4</c>); the assembly version when there is none, and <c>0.0.0</c> when there is neither.
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
