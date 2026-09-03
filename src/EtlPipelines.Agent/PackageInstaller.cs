using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent;

/// <summary>
/// Installs a package as a real <c>dotnet tool</c> - <c>dotnet tool install --tool-path ...
/// --add-source ... &lt;packageId&gt; --version &lt;version&gt;</c>, run via <c>CliWrap</c> (the
/// shape <c>EtlPipelines.Extensions.Cli</c>'s <c>CliCommandStage</c> already establishes for
/// exit-code/stderr handling). No manual <c>.nupkg</c> download, no <c>ZipFile</c> extraction, no
/// <c>NuGet.Packaging</c> - the <c>dotnet</c> SDK already does all of that, on every platform,
/// more robustly than a hand-rolled version would (Phase 1).
/// </summary>
public sealed class PackageInstaller(IOptions<AgentOptions> options)
{
    /// <summary>Installs <paramref name="packageId"/> <paramref name="version"/> and returns the installed shim's full path.</summary>
    public async Task<string> InstallAsync(
        string packageId, string version, IEnumerable<string> feedUrls, CancellationToken cancellationToken)
    {
        var installDirectory = Path.Combine(options.Value.CacheDirectory, packageId, version);
        Directory.CreateDirectory(installDirectory);

        // `--add-source`/`--source` add a source location, but say nothing about whether that
        // source is allowed to be plain HTTP - confirmed the hard way, against a real bagetter
        // container: `dotnet tool install --add-source http://...` fails outright with "NuGet
        // requires HTTPS sources... you must explicitly set 'allowInsecureConnections' to true in
        // your NuGet.Config file", and unlike `dotnet nuget push`, `dotnet tool install` has no
        // `--allow-insecure-connections` flag - `allowInsecureConnections` only exists as a
        // NuGet.Config <packageSources> attribute. A generated, per-install NuGet.Config (passed
        // via --configfile) is what actually grants it; <clear /> keeps this install scoped to
        // exactly the feed(s) the server dispatched, not whatever ambient sources happen to be
        // configured on the machine running this agent.
        //
        // Written to its own temp directory, not installDirectory itself - confirmed the hard way
        // again: dropped alongside the shim, FindShim's own "exactly one file" assumption about
        // --tool-path's contents broke the moment a second real file (this one) showed up next to
        // it.
        var configDirectory = Directory.CreateTempSubdirectory("EtlPipelines.Agent.PackageInstaller.");

        try
        {
            var configPath = Path.Combine(configDirectory.FullName, "NuGet.Config");
            await File.WriteAllTextAsync(configPath, BuildNuGetConfigXml(feedUrls), cancellationToken).ConfigureAwait(false);

            var arguments = new List<string>
            {
                "tool", "install", "--tool-path", installDirectory, "--version", version, "--configfile", configPath,
            };

            // No --prerelease here, deliberately, unlike Phase 1's own packaging test: that test
            // asks for "whatever's latest" with no --version, where --prerelease is what makes a
            // Nerdbank.GitVersioning-stamped package (e.g. "1.0.0-g...") eligible at all. Confirmed
            // empirically, the hard way, right here: `dotnet tool install` refuses --prerelease
            // together with an explicit --version outright ("not supported in the same command") -
            // and since every install this class ever runs is a specific version
            // PackageCatalogService already resolved server-side, there is never a "latest" to
            // widen eligibility for. A pinned prerelease version installs here with no flag needed
            // either way.
            arguments.Add(packageId);

            BufferedCommandResult result;
            try
            {
                result = await Cli.Wrap("dotnet")
                    .WithArguments(arguments)
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                throw new InvalidOperationException("'dotnet' could not be started.", exception);
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"dotnet tool install failed for '{packageId}' {version}: {Tail(result)}");
            }

            return FindShim(installDirectory);
        }
        finally
        {
            try
            {
                Directory.Delete(configDirectory.FullName, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing an install over.
            }
        }
    }

    /// <summary>
    /// <c>dotnet tool install --tool-path</c> drops the generated shim directly in the tool-path
    /// directory, alongside a <c>.store</c> subdirectory holding the actual package contents - the
    /// same discovery Phase 1's own packaging test uses, since the shim's exact name (casing,
    /// extension) is a platform/SDK-version detail not worth hardcoding a guess about.
    /// </summary>
    private static string FindShim(string installDirectory) =>
        Directory.EnumerateFiles(installDirectory).SingleOrDefault(path => !Path.GetFileName(path).StartsWith('.'))
        ?? throw new InvalidOperationException($"dotnet tool install did not leave a shim directly under {installDirectory}.");

    /// <summary>A minimal NuGet.Config granting exactly <paramref name="feedUrls"/>, each marked <c>allowInsecureConnections</c>.</summary>
    private static string BuildNuGetConfigXml(IEnumerable<string> feedUrls)
    {
        var sources = feedUrls.Select((url, index) =>
            $"""    <add key="feed{index}" value="{System.Security.SecurityElement.Escape(url)}" allowInsecureConnections="true" />""");

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
            {string.Join(Environment.NewLine, sources)}
              </packageSources>
            </configuration>
            """;
    }

    private static string Tail(BufferedCommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() : "(no output)";
    }
}
