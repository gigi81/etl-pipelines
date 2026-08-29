using System.IO.Abstractions.TestingHelpers;

namespace EtlPipelines.Files.Tests;

public sealed class ArchiveRoundTripTests
{
    [Test]
    [Arguments("archive.zip")]
    [Arguments("archive.tar")]
    [Arguments("archive.tar.gz")]
    public async Task Round_trips_files_through_the_container_formats(string archiveName)
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("aaa"));
        fs.AddFile("/work/in/b.csv", new MockFileData("bbb"));

        var archive = fs.FileInfo.New($"/work/{archiveName}");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("roundtrip")
            .CompressFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", archive)
            .ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.ReadAllText("/work/extracted/a.csv").Should().Be("aaa");
        fs.File.ReadAllText("/work/extracted/b.csv").Should().Be("bbb");
    }

    [Test]
    public async Task Compresses_a_single_file_to_gzip()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/report.csv", new MockFileData("id,customer"));

        var archive = fs.FileInfo.New("/work/report.csv.gz");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("gzip")
            .CompressFile(fs.FileInfo.New("/work/report.csv"), archive)
            .ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.ReadAllText("/work/extracted/report.csv").Should().Be("id,customer");
    }

    [Test]
    public async Task Fails_when_gzip_is_given_more_than_one_file()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("a"));
        fs.AddFile("/work/in/b.csv", new MockFileData("b"));

        var builder = EtlPipeline.CreateBuilder("gzip")
            .CompressFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", fs.FileInfo.New("/work/out.gz"));

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".gzip_single_file");
        fs.File.Exists("/work/out.gz").Should().BeFalse();
    }

    [Test]
    public async Task Preserves_directory_structure_by_default()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/orders/a.csv", new MockFileData("a"));

        var archive = fs.FileInfo.New("/work/archive.zip");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("zip")
            .CompressFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", archive, o => o.Recursive = true)
            .ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/extracted/orders/a.csv").Should().BeTrue();
    }

    [Test]
    public async Task Flattens_paths_when_asked()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/orders/a.csv", new MockFileData("a"));

        var archive = fs.FileInfo.New("/work/archive.zip");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("zip")
            .CompressFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", archive, o =>
            {
                o.Recursive = true;
                o.FlattenPaths = true;
            })
            .ExtractArchive(archive, extracted);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/extracted/a.csv").Should().BeTrue();
        fs.File.Exists("/work/extracted/orders/a.csv").Should().BeFalse();
    }

    [Test]
    public async Task Writes_the_archive_atomically()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("a"));
        var archive = fs.FileInfo.New("/work/archive.zip");

        var builder = EtlPipeline.CreateBuilder("zip")
            .CompressFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", archive);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        var leftovers = fs.Directory.EnumerateFiles("/work", "*.tmp", SearchOption.AllDirectories);
        leftovers.Should().BeEmpty("the temporary sibling is renamed away, not left beside a successful archive");
    }

    [Test]
    public async Task Refuses_an_archive_with_more_than_MaxEntries()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("a"));
        fs.AddFile("/work/in/b.csv", new MockFileData("b"));

        var archive = fs.FileInfo.New("/work/archive.zip");
        var extracted = fs.DirectoryInfo.New("/work/extracted");

        var builder = EtlPipeline.CreateBuilder("zip")
            .CompressFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", archive)
            .ExtractArchive(archive, extracted, o => o.MaxEntries = 1);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".too_many_entries");
    }
}
