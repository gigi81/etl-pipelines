namespace EtlPipelines.Agent.Tests;

/// <summary>
/// Fast tests for <see cref="CacheEvictor.Sweep"/>/<see cref="CacheEvictor.Touch"/> - SERVER.md
/// Phase 8's own instruction ("fast tests for... the LRU sweep"). Drives real, small files under a
/// temp directory rather than mocking the filesystem - <see cref="CacheEvictor"/> is deliberately
/// plain static methods against real I/O for exactly this reason (see that class's own remarks).
/// </summary>
[Category("Agent")]
public class CacheEvictorTests
{
    [Test]
    public async Task Sweep_does_nothing_when_under_the_size_cap()
    {
        //arrange
        using var root = new TempCacheDirectory();
        var install = root.AddInstall("PackageA", "1.0.0", sizeBytes: 100, lastUsedUtc: DateTime.UtcNow);

        //act
        CacheEvictor.Sweep(root.Path, sizeCapBytes: 1_000);

        //assert
        await Assert.That(Directory.Exists(install)).IsTrue();
    }

    [Test]
    public async Task Sweep_deletes_the_least_recently_used_install_first_once_over_the_cap()
    {
        //arrange
        using var root = new TempCacheDirectory();
        var now = DateTime.UtcNow;
        var oldest = root.AddInstall("PackageA", "1.0.0", sizeBytes: 600, lastUsedUtc: now - TimeSpan.FromDays(3));
        var middle = root.AddInstall("PackageA", "2.0.0", sizeBytes: 600, lastUsedUtc: now - TimeSpan.FromDays(1));
        var newest = root.AddInstall("PackageB", "1.0.0", sizeBytes: 600, lastUsedUtc: now);

        //act - 1800 total, cap 1000: deleting the oldest alone (600) only brings it to 1200,
        // still over the cap, so the two least-recently-used installs must both go, leaving only
        // the most recently used.
        CacheEvictor.Sweep(root.Path, sizeCapBytes: 1_000);

        //assert
        await Assert.That(Directory.Exists(oldest)).IsFalse();
        await Assert.That(Directory.Exists(middle)).IsFalse();
        await Assert.That(Directory.Exists(newest)).IsTrue();
    }

    [Test]
    public async Task Sweep_stops_as_soon_as_the_total_is_back_at_or_under_the_cap()
    {
        //arrange
        using var root = new TempCacheDirectory();
        var now = DateTime.UtcNow;
        var oldest = root.AddInstall("PackageA", "1.0.0", sizeBytes: 400, lastUsedUtc: now - TimeSpan.FromDays(2));
        var newest = root.AddInstall("PackageA", "2.0.0", sizeBytes: 400, lastUsedUtc: now);

        //act - 800 total, cap 500: deleting just the oldest (400) brings it to 400, already under
        // the cap, so the newest one must survive.
        CacheEvictor.Sweep(root.Path, sizeCapBytes: 500);

        //assert
        await Assert.That(Directory.Exists(oldest)).IsFalse();
        await Assert.That(Directory.Exists(newest)).IsTrue();
    }

    [Test]
    public async Task Touch_makes_an_install_the_most_recently_used_even_if_it_is_the_smallest()
    {
        //arrange
        using var root = new TempCacheDirectory();
        var now = DateTime.UtcNow;
        var untouched = root.AddInstall("PackageA", "1.0.0", sizeBytes: 400, lastUsedUtc: now - TimeSpan.FromDays(5));
        var justTouched = root.AddInstall("PackageA", "2.0.0", sizeBytes: 400, lastUsedUtc: now - TimeSpan.FromDays(5));

        //act
        CacheEvictor.Touch(justTouched);
        CacheEvictor.Sweep(root.Path, sizeCapBytes: 400);

        //assert - both started with the same last-used time; only Touch should have changed that.
        await Assert.That(Directory.Exists(untouched)).IsFalse();
        await Assert.That(Directory.Exists(justTouched)).IsTrue();
    }

    [Test]
    public void Sweep_against_a_cache_directory_that_does_not_exist_yet_does_nothing()
    {
        //arrange
        var missingDirectory = Path.Combine(Path.GetTempPath(), $"etlpipelines-cache-evictor-missing-{Guid.NewGuid():N}");

        //act & assert - a fresh agent-cache before anything has ever been installed is exactly
        // this case; the test fails outright if this throws.
        CacheEvictor.Sweep(missingDirectory, sizeCapBytes: 0);
    }

    /// <summary>A real, disposable <c>&lt;root&gt;/&lt;packageId&gt;/&lt;version&gt;</c> tree, matching <see cref="PackageInstaller"/>'s own cache layout.</summary>
    private sealed class TempCacheDirectory : IDisposable
    {
        public TempCacheDirectory() => Path = Directory.CreateTempSubdirectory("EtlPipelines.Agent.Tests.CacheEvictor.").FullName;

        public string Path { get; }

        /// <summary>Creates <c>&lt;Path&gt;/&lt;packageId&gt;/&lt;version&gt;/payload.bin</c> of exactly <paramref name="sizeBytes"/>, then backdates its last-used time.</summary>
        public string AddInstall(string packageId, string version, long sizeBytes, DateTime lastUsedUtc)
        {
            var installDirectory = System.IO.Path.Combine(Path, packageId, version);
            Directory.CreateDirectory(installDirectory);
            File.WriteAllBytes(System.IO.Path.Combine(installDirectory, "payload.bin"), new byte[sizeBytes]);

            // CacheEvictor.Touch always stamps "now" - this writes the marker the same way, then
            // sets its time explicitly, which is the only part a real Touch call could never do
            // (these tests need installs that already look old without a real elapsed wait).
            var markerPath = System.IO.Path.Combine(installDirectory, ".last-used");
            File.WriteAllBytes(markerPath, []);
            File.SetLastWriteTimeUtc(markerPath, lastUsedUtc);

            return installDirectory;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
