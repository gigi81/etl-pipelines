using System.Formats.Tar;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Text;

namespace EtlPipelines.Files.Tests;

/// <summary>
/// Every entry name here is something a well-behaved archiver would never write, and something a
/// hostile one - or a corrupted one - might. Each must be refused before extraction writes anything.
/// </summary>
public sealed class ZipSlipTests
{
    [Test]
    [Arguments("../evil.txt")]
    [Arguments("../../evil.txt")]
    [Arguments("a/../../evil.txt")]
    public async Task Refuses_an_entry_that_escapes_with_dot_dot(string entryName)
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var archive = WriteMaliciousZip(fs, "/work/evil.zip", entryName);
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".unsafe_entry");
        fs.File.Exists("/work/evil.txt").Should().BeFalse();
    }

    [Test]
    [Arguments("/etc/passwd")]
    [Arguments(@"\etc\passwd")]
    public async Task Refuses_a_rooted_entry_name(string entryName)
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var archive = WriteMaliciousZip(fs, "/work/evil.zip", entryName);
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".unsafe_entry");
    }

    [Test]
    public async Task Refuses_a_windows_drive_qualified_entry_name_on_every_platform()
    {
        //arrange
        // Path.IsPathRooted("C:\\payload") is false when this test runs on Linux - the drive check
        // must be textual, not delegated to the host's own path rules.
        var fs = Fixtures.NewFileSystem();
        var archive = WriteMaliciousZip(fs, "/work/evil.zip", @"C:\payload.txt");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".unsafe_entry");
    }

    [Test]
    public async Task Refuses_a_unc_entry_name()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var archive = WriteMaliciousZip(fs, "/work/evil.zip", @"\\server\share\payload.txt");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".unsafe_entry");
    }

    [Test]
    public async Task Refuses_a_sibling_directory_with_a_shared_prefix()
    {
        //arrange
        // The naive check `resolved.StartsWith(root)` wrongly admits "/work/extracted-evil" for root
        // "/work/extracted" - the comparison must be against the root plus a trailing separator.
        var fs = Fixtures.NewFileSystem();
        var archive = WriteMaliciousZip(fs, "/work/evil.zip", "../extracted-evil/payload.txt");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".unsafe_entry");
        fs.File.Exists("/work/extracted-evil/payload.txt").Should().BeFalse();
    }

    [Test]
    public async Task Refuses_a_tar_symbolic_link_entry_by_default()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var archive = fs.FileInfo.New("/work/evil.tar");

        await using (var stream = archive.Create())
        await using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var link = new PaxTarEntry(TarEntryType.SymbolicLink, "link.txt") { LinkName = "/etc/passwd" };
            await writer.WriteEntryAsync(link, CancellationToken.None);
        }

        var extracted = fs.DirectoryInfo.New("/work/extracted");
        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null,
            "Skip is the default - the link is dropped, not created, and extraction still succeeds");
        fs.File.Exists("/work/extracted/link.txt").Should().BeFalse();
    }

    [Test]
    public async Task Fails_the_whole_extraction_for_a_link_entry_under_LinkPolicy_Fail()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var archive = fs.FileInfo.New("/work/evil.tar");

        await using (var stream = archive.Create())
        await using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var link = new PaxTarEntry(TarEntryType.SymbolicLink, "link.txt") { LinkName = "/etc/passwd" };
            await writer.WriteEntryAsync(link, CancellationToken.None);
        }

        var extracted = fs.DirectoryInfo.New("/work/extracted");
        var builder = EtlPipeline.CreateBuilder("extract")
            .ExtractArchive(archive, extracted, o => o.Links = LinkPolicy.Fail);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".unsafe_entry");
    }

    [Test]
    public async Task Writes_nothing_at_all_when_an_entry_is_unsafe()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var archive = WriteMaliciousZip(fs, "/work/evil.zip", "safe.txt", "../evil.txt");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        // The empty target directory may exist - CreateTargetDirectory does that up front - but the
        // validation pass runs before any entry, safe or not, is written into it.
        fs.Directory.GetFileSystemEntries("/work/extracted").Should().BeEmpty();
    }

    [Test]
    public async Task Accepts_a_nested_path_that_genuinely_stays_inside()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var archive = WriteMaliciousZip(fs, "/work/fine.zip", "orders/2026/a.csv");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("extract").ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/extracted/orders/2026/a.csv").Should().BeTrue();
    }

    /// <summary>Writes a zip whose entries are named exactly as given - CreateEntry does not itself validate them.</summary>
    private static System.IO.Abstractions.IFileInfo WriteMaliciousZip(
        MockFileSystem fs, string path, params string[] entryNames)
    {
        var file = fs.FileInfo.New(path);

        using var stream = file.Create();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var entryName in entryNames)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes("payload");
            entryStream.Write(bytes, 0, bytes.Length);
        }

        return file;
    }
}
