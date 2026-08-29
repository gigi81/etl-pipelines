using System.IO.Abstractions;

namespace EtlPipelines.Files.Tests;

/// <summary>
/// A few of the same operations against the real disk.
/// </summary>
/// <remarks>
/// <c>MockFileSystem</c> has its own matcher and its own idea of what a rename does, and both are
/// reimplementations that can diverge from the real thing at exactly the edges this package cares
/// about - pattern semantics and atomic promotion. These tests exist so a divergence shows up as a
/// failure here rather than as a production surprise.
/// </remarks>
public sealed class FilesRealFileSystemTests : IDisposable
{
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly IDirectoryInfo _root;

    public FilesRealFileSystemTests()
    {
        _root = _fileSystem.Directory.CreateTempSubdirectory("etl-files-");
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test over.
        }
    }

    [Test]
    public async Task Does_not_match_html_for_a_htm_pattern()
    {
        //arrange
        // The Windows default, MatchType.Win32, carries DOS-8.3 semantics under which "*.htm" also
        // matches "report.html". PatternFileSelection asks for MatchType.Simple specifically so a
        // pattern means the same thing on every host, and only the real filesystem's own matcher can
        // actually prove that - MockFileSystem has its own.
        await _fileSystem.File.WriteAllTextAsync(_root.File("report.html").FullName, "x");
        await _fileSystem.File.WriteAllTextAsync(_root.File("report.htm").FullName, "x");

        var selection = new PatternFileSelection(_root, "*.htm", new FileCopyOptions());

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();
        result.Value.Select(f => f.Name).Should().Equal("report.htm");
    }

    [Test]
    public async Task Extracts_a_real_zip_produced_by_the_bcl()
    {
        //arrange
        var source = _root.File("a.csv");
        await _fileSystem.File.WriteAllTextAsync(source.FullName, "id,customer");
        var archive = _root.File("archive.zip");
        var extracted = _root.SubDirectory("extracted");

        var builder = EtlPipeline.CreateBuilder("zip")
            .CompressFile(source, archive)
            .ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _fileSystem.File.ReadAllTextAsync(extracted.File("a.csv").FullName)).Should().Be("id,customer");
    }

    [Test]
    public async Task Round_trips_a_tar_gz_on_a_real_disk()
    {
        //arrange
        var source = _root.File("a.csv");
        await _fileSystem.File.WriteAllTextAsync(source.FullName, "id,customer");
        var archive = _root.File("archive.tar.gz");
        var extracted = _root.SubDirectory("extracted");

        var builder = EtlPipeline.CreateBuilder("targz")
            .CompressFile(source, archive)
            .ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _fileSystem.File.ReadAllTextAsync(extracted.File("a.csv").FullName)).Should().Be("id,customer");
    }

    [Test]
    public async Task Moves_a_file_atomically_within_one_volume()
    {
        //arrange
        var source = _root.File("a.csv");
        var sourcePath = source.FullName; // MoveTo updates the IFileInfo's own FullName in place
        await _fileSystem.File.WriteAllTextAsync(sourcePath, "id,customer");
        var target = _root.SubDirectory("out").File("moved.csv");

        var builder = EtlPipeline.CreateBuilder("move").MoveFile(source, target);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        _fileSystem.File.Exists(sourcePath).Should().BeFalse();
        _fileSystem.File.Exists(target.FullName).Should().BeTrue();
    }
}
