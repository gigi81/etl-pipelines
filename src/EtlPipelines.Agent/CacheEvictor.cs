using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent;

/// <summary>
/// LRU sweep over <see cref="AgentOptions.CacheDirectory"/> - SERVER.md Phase 8's "agent-cache...
/// grows unboundedly through Phase 7" gap. Every installed package version
/// (<c>&lt;CacheDirectory&gt;/&lt;packageId&gt;/&lt;version&gt;</c>, <see cref="PackageInstaller"/>'s
/// own layout) is "used" whenever this agent installs or runs it - <see cref="Touch"/>, called from
/// both <see cref="PackageInstaller.InstallAsync"/> and <see cref="PackageInstaller.GetInstalledShimPath"/>,
/// stamps a marker file's write time rather than relying on filesystem access-time tracking, which
/// many mounts (the Docker volume this cache directory becomes in Phase 7 included) disable for
/// performance and would otherwise silently make every install look equally "never accessed".
/// </summary>
/// <remarks>
/// Only <c>agent-cache</c> - <c>server-cache</c> (Phase 7's other named-as-growing volume) holds
/// nothing installed-tool-shaped to sweep: <c>Server</c> never downloads or installs a package
/// itself (see SERVER.md's "Install validation" decision), so the only thing that volume actually
/// holds is the Data Protection key ring, which must never be evicted. <see cref="Sweep"/> is a
/// plain static method, deliberately: it touches only the real filesystem, so a fast test drives it
/// directly against a temp directory rather than through this class's own <see cref="BackgroundService"/>
/// timer loop - see <c>CacheEvictorTests</c>.
/// </remarks>
public sealed class CacheEvictor(IOptions<AgentOptions> options, ILogger<CacheEvictor> logger) : BackgroundService
{
    private const string LastUsedMarkerFileName = ".last-used";

    /// <summary>Marks an installed package version's <c>&lt;packageId&gt;/&lt;version&gt;</c> directory as just used.</summary>
    public static void Touch(string installDirectory)
    {
        try
        {
            File.WriteAllBytes(Path.Combine(installDirectory, LastUsedMarkerFileName), []);
        }
        catch (IOException)
        {
            // Losing track of one install's last-used time only makes it a slightly worse
            // eviction candidate than it should be next sweep - never worth failing an install or
            // a run over.
        }
    }

    /// <summary>
    /// Deletes the least-recently-<see cref="Touch"/>ed installed package version(s) under
    /// <paramref name="cacheDirectory"/> until its total size is at or under
    /// <paramref name="sizeCapBytes"/>.
    /// </summary>
    public static void Sweep(string cacheDirectory, long sizeCapBytes)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return;
        }

        var installs = EnumerateInstalls(cacheDirectory).OrderBy(install => install.LastUsedAtUtc).ToList();
        var totalSize = installs.Sum(install => install.SizeBytes);

        foreach (var install in installs)
        {
            if (totalSize <= sizeCapBytes)
            {
                break;
            }

            try
            {
                Directory.Delete(install.Path, recursive: true);
                totalSize -= install.SizeBytes;
            }
            catch (IOException)
            {
                // Most likely a version this agent is actively running right now (Windows locks
                // an in-use executable's file; even where the OS itself would tolerate deleting a
                // running one, this is still the safer default) - left in place, tried again next
                // sweep, rather than failing the whole sweep over one directory.
            }
            catch (UnauthorizedAccessException)
            {
                // Same reasoning as IOException above.
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CacheEvictionInterval);

        do
        {
            try
            {
                Sweep(options.Value.CacheDirectory, options.Value.CacheSizeCapBytes);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed sweep never stops future ones, same reasoning as every other periodic
                // loop in this codebase.
                logger.LogWarning(exception, "Cache eviction sweep failed; will retry on the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private static IEnumerable<InstalledVersion> EnumerateInstalls(string cacheDirectory) =>
        from packageDirectory in Directory.EnumerateDirectories(cacheDirectory)
        from versionDirectory in Directory.EnumerateDirectories(packageDirectory)
        select new InstalledVersion(versionDirectory, DirectorySize(versionDirectory), LastUsedUtc(versionDirectory));

    private static long DirectorySize(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    /// <summary>
    /// The marker file's write time if <see cref="Touch"/> has ever run against this install;
    /// otherwise the directory's own creation time - an install from before this feature existed,
    /// or one whose <see cref="Touch"/> call itself failed, is not immortal just because it has no
    /// marker.
    /// </summary>
    private static DateTime LastUsedUtc(string installDirectory)
    {
        var markerPath = Path.Combine(installDirectory, LastUsedMarkerFileName);
        return File.Exists(markerPath) ? File.GetLastWriteTimeUtc(markerPath) : Directory.GetCreationTimeUtc(installDirectory);
    }

    private sealed record InstalledVersion(string Path, long SizeBytes, DateTime LastUsedAtUtc);
}
