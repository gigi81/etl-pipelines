using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace EtlPipelines.Files.Tests;

public sealed class CopyAndMoveTests
{
    [Test]
    public async Task Copies_one_file_and_leaves_the_source()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/source.csv", new MockFileData("id,customer"));
        var source = fs.FileInfo.New("/work/source.csv");
        var target = fs.FileInfo.New("/work/out/copy.csv");

        var builder = EtlPipeline.CreateBuilder("copy").CopyFile(source, target);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        source.Refresh();
        source.Exists.Should().BeTrue();
        fs.File.ReadAllText("/work/out/copy.csv").Should().Be("id,customer");
    }

    [Test]
    public async Task Moves_one_file_and_removes_the_source()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/source.csv", new MockFileData("id"));
        var source = fs.FileInfo.New("/work/source.csv");
        var target = fs.FileInfo.New("/work/out/moved.csv");

        var builder = EtlPipeline.CreateBuilder("move").MoveFile(source, target);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/source.csv").Should().BeFalse();
        fs.File.Exists("/work/out/moved.csv").Should().BeTrue();
    }

    [Test]
    public async Task Copies_every_matching_file_into_the_target_directory()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("a"));
        fs.AddFile("/work/in/b.csv", new MockFileData("b"));
        fs.AddFile("/work/in/c.txt", new MockFileData("c"));

        var builder = EtlPipeline.CreateBuilder("copy-many")
            .CopyFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", fs.DirectoryInfo.New("/work/out"));

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/out/a.csv").Should().BeTrue();
        fs.File.Exists("/work/out/b.csv").Should().BeTrue();
        fs.File.Exists("/work/out/c.txt").Should().BeFalse();
    }

    [Test]
    public async Task Creates_the_target_directory_when_it_does_not_exist()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/source.csv", new MockFileData("id"));

        var builder = EtlPipeline.CreateBuilder("copy")
            .CopyFile(fs.FileInfo.New("/work/source.csv"), fs.FileInfo.New("/work/deep/nested/copy.csv"));

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.Exists("/work/deep/nested/copy.csv").Should().BeTrue();
    }

    [Test]
    public async Task Overwrites_an_existing_target_by_default()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/source.csv", new MockFileData("new"));
        fs.AddFile("/work/out/copy.csv", new MockFileData("old"));

        var builder = EtlPipeline.CreateBuilder("copy")
            .CopyFile(fs.FileInfo.New("/work/source.csv"), fs.FileInfo.New("/work/out/copy.csv"));

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.ReadAllText("/work/out/copy.csv").Should().Be("new");
    }

    [Test]
    public async Task Skips_an_existing_target_under_Skip()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/source.csv", new MockFileData("new"));
        fs.AddFile("/work/out/copy.csv", new MockFileData("old"));

        var builder = EtlPipeline.CreateBuilder("copy")
            .CopyFile(
                fs.FileInfo.New("/work/source.csv"),
                fs.FileInfo.New("/work/out/copy.csv"),
                o => o.Overwrite = OverwritePolicy.Skip);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        fs.File.ReadAllText("/work/out/copy.csv").Should().Be("old", "Skip leaves an existing target alone");
    }

    [Test]
    public async Task Fails_before_writing_anything_under_Fail()
    {
        //arrange
        // Two files selected; the second's target already exists. Fail must refuse the whole batch
        // before the first file - which would otherwise succeed - is written.
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("a"));
        fs.AddFile("/work/in/b.csv", new MockFileData("b"));
        fs.AddFile("/work/out/b.csv", new MockFileData("already here"));

        var builder = EtlPipeline.CreateBuilder("copy").CopyFiles(
            fs.DirectoryInfo.New("/work/in"), "*.csv", fs.DirectoryInfo.New("/work/out"),
            o => o.Overwrite = OverwritePolicy.Fail);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Code.Should().EndWith(".exists");
        fs.File.Exists("/work/out/a.csv").Should().BeFalse("Fail must not write anything once one target is found to exist");
    }

    [Test]
    public async Task Keeps_the_files_it_already_moved_when_a_later_one_fails()
    {
        //arrange
        // b.csv is selected but never actually written to disk, so reading it to move it throws.
        // a.csv is real and sorts first, and must have already landed, with its source gone, by the
        // time that happens.
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("a"));

        var selection = new FixedFileSelection(
            "in", fs.FileInfo.New("/work/in/a.csv"), fs.FileInfo.New("/work/in/b.csv"));

        var builder = EtlPipeline.CreateBuilder("move")
            .MoveFiles(selection, fs.DirectoryInfo.New("/work/out"));

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("file 2 of 2").And.Contain("b.csv");

        fs.File.Exists("/work/out/a.csv").Should().BeTrue("the file that already succeeded stays moved");
        fs.File.Exists("/work/in/a.csv").Should().BeFalse("its source was removed once the move completed");
    }

    [Test]
    public async Task Publishes_what_it_produced_for_a_later_stage_to_read()
    {
        //arrange
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/a.csv", new MockFileData("a"));
        fs.AddFile("/work/in/b.csv", new MockFileData("b"));

        IReadOnlyList<string>? produced = null;

        var builder = EtlPipeline.CreateBuilder("copy")
            .CopyFiles(fs.DirectoryInfo.New("/work/in"), "*.csv", fs.DirectoryInfo.New("/work/out"), o => o.PublishAs = "inbox")
            .AddStage("check", (ctx, _) =>
            {
                produced = [.. ctx.ProducedFiles("inbox").Select(f => f.Name)];
                return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
            });

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        produced.Should().BeEquivalentTo(["a.csv", "b.csv"]);
    }

    [Test]
    public async Task Reports_every_failure_under_FileErrorAction_Continue()
    {
        //arrange
        // a.csv and b.csv are selected but never written to disk, so copying either throws. c.csv is
        // real and sorts last, and Continue must still attempt it after the first two fail.
        var fs = Fixtures.NewFileSystem();
        fs.AddFile("/work/in/c.csv", new MockFileData("c"));

        var selection = new FixedFileSelection(
            "in",
            fs.FileInfo.New("/work/in/a.csv"),
            fs.FileInfo.New("/work/in/b.csv"),
            fs.FileInfo.New("/work/in/c.csv"));

        var builder = EtlPipeline.CreateBuilder("copy").CopyFiles(
            selection, fs.DirectoryInfo.New("/work/out"),
            o => o.OnFileError = FileErrorAction.Continue);

        //act
        var result = await builder.Build().RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("2 of 3 files failed").And.Contain("a.csv").And.Contain("b.csv");
        fs.File.Exists("/work/out/c.csv").Should().BeTrue("a file after two failures still gets attempted under Continue");
    }
}
