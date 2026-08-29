using System.IO.Abstractions.TestingHelpers;

namespace EtlPipelines.Extensions.Files.Tests;

public sealed class FileSelectionTests
{
    [Test]
    public async Task Resolves_a_single_named_file()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/orders.csv", new MockFileData("id,customer"));
        var selection = new SingleFileSelection(fs.FileInfo.New("/work/orders.csv"));

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();
        result.Value.Should().ContainSingle().Which.Name.Should().Be("orders.csv");
    }

    [Test]
    public async Task Fails_when_a_named_file_is_missing()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        var selection = new SingleFileSelection(fs.FileInfo.New("/work/missing.csv"));

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("files.selection.missing");
    }

    [Test]
    public async Task Resolves_a_file_an_earlier_stage_wrote()
    {
        //arrange
        // IFileInfo caches what it found when it was created - the file is named before it exists,
        // and only written afterwards. Selection must refresh, or this never sees it.
        var fs = Fixtures.NewFileSystem();
        var file = fs.FileInfo.New("/work/late.csv");
        var selection = new SingleFileSelection(file);

        fs.AddFile("/work/late.csv", new MockFileData("id"));

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();
    }

    [Test]
    public async Task Matches_a_star_pattern_and_ignores_the_rest()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/a.csv", new MockFileData("x"));
        fs.AddFile("/work/b.csv", new MockFileData("x"));
        fs.AddFile("/work/c.txt", new MockFileData("x"));

        var selection = new PatternFileSelection(fs.DirectoryInfo.New("/work"), "*.csv", new FileCopyOptions());

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse();
        result.Value.Select(f => f.Name).Should().BeEquivalentTo(["a.csv", "b.csv"]);
    }

    [Test]
    public async Task Returns_no_files_for_a_pattern_that_matches_nothing()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddDirectory("/work/inbox");
        var selection = new PatternFileSelection(fs.DirectoryInfo.New("/work/inbox"), "*.csv", new FileCopyOptions());

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse("an empty inbox is a normal night, not an error, unless MinimumFiles says otherwise");
        result.Value.Should().BeEmpty();
    }

    [Test]
    public async Task Fails_when_fewer_than_MinimumFiles_match()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/a.csv", new MockFileData("x"));
        var selection = new PatternFileSelection(
            fs.DirectoryInfo.New("/work"), "*.csv", new FileCopyOptions { MinimumFiles = 2 });

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().Be("files.selection.empty");
    }

    [Test]
    public async Task Orders_by_name_ordinally_by_default()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/c.csv", new MockFileData("x"));
        fs.AddFile("/work/a.csv", new MockFileData("x"));
        fs.AddFile("/work/b.csv", new MockFileData("x"));
        var selection = new PatternFileSelection(fs.DirectoryInfo.New("/work"), "*.csv", new FileCopyOptions());

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.Value.Select(f => f.Name).Should().Equal("a.csv", "b.csv", "c.csv");
    }

    [Test]
    public async Task Orders_by_last_write_time_when_asked()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/newer.csv", new MockFileData("x") { LastWriteTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero) });
        fs.AddFile("/work/older.csv", new MockFileData("x") { LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        var selection = new PatternFileSelection(
            fs.DirectoryInfo.New("/work"), "*.csv", new FileCopyOptions { Order = FileOrder.LastWriteTime });

        //act
        var result = await selection.ResolveAsync(CancellationToken.None);

        //assert
        result.Value.Select(f => f.Name).Should().Equal("older.csv", "newer.csv");
    }

    [Test]
    public async Task Recurses_only_when_asked()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/top.csv", new MockFileData("x"));
        fs.AddFile("/work/nested/deep.csv", new MockFileData("x"));

        var flat = new PatternFileSelection(fs.DirectoryInfo.New("/work"), "*.csv", new FileCopyOptions());
        var recursive = new PatternFileSelection(
            fs.DirectoryInfo.New("/work"), "*.csv", new FileCopyOptions { Recursive = true });

        //act
        var flatResult = await flat.ResolveAsync(CancellationToken.None);
        var recursiveResult = await recursive.ResolveAsync(CancellationToken.None);

        //assert
        flatResult.Value.Select(f => f.Name).Should().Equal("top.csv");
        recursiveResult.Value.Select(f => f.Name).Should().BeEquivalentTo(["top.csv", "deep.csv"]);
    }
}
