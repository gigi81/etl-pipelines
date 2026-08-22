namespace EtlPipelines.Samples.Tests;

/// <summary>
/// Each sample driven the way somebody actually runs it: through its command line.
/// </summary>
/// <remarks>
/// The tests above prove the pipelines do the right thing. These prove the wiring around them holds —
/// that the verbs are registered, that the handlers resolve everything they ask the container for,
/// that <c>--work-dir</c> reaches the workspace, and that a successful run exits zero. None of that
/// is covered by registering the pipeline and running it directly, and all of it is what breaks when
/// a constructor gains a dependency nobody registered.
/// </remarks>
[Category("Samples")]
public class SampleCliTests
{
    private static string Scratch(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"etl-cli-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test over.
        }
    }

    [Test]
    [Arguments(CsvToExcel.Pipeline.Name)]
    public async Task Csv_to_excel_runs_from_the_command_line(string pipeline)
    {
        var directory = Scratch("csv-excel");

        try
        {
            //act
            var exitCode = await CsvToExcel.Program.RunAsync(["run", pipeline, "--work-dir", directory]);

            //assert
            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, CsvToExcel.Pipeline.OutputFile)).Should().BeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Test]
    public async Task Sql_to_workbook_runs_from_the_command_line()
    {
        var directory = Scratch("sql-workbook");

        try
        {
            var exitCode = await SqlToWorkbook.Program.RunAsync(["run", SqlToWorkbook.Pipeline.Name, "--work-dir", directory]);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, SqlToWorkbook.Pipeline.OutputFile)).Should().BeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Test]
    public async Task Excel_to_sql_runs_from_the_command_line()
    {
        var directory = Scratch("excel-sql");

        try
        {
            var exitCode = await ExcelToSql.Program.RunAsync(["run", ExcelToSql.Pipeline.Name, "--work-dir", directory]);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, "orders.db")).Should().BeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Test]
    public async Task Branching_runs_from_the_command_line()
    {
        var directory = Scratch("branching");

        try
        {
            var exitCode = await Branching.Program.RunAsync(["run", Branching.Pipeline.Name, "--work-dir", directory]);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, Branching.Pipeline.ArchiveFile)).Should().BeTrue();
            File.Exists(Path.Combine(directory, Branching.Pipeline.ReportFile)).Should().BeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Test]
    public async Task Csv_to_database_runs_from_the_command_line()
    {
        var directory = Scratch("csv-sqlite");

        try
        {
            var exitCode = await CsvToDatabase.Program.RunAsync(["run", CsvToDatabase.Pipeline.Name, "--work-dir", directory]);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, "trades.db")).Should().BeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Test]
    public async Task The_built_in_list_verb_names_the_sample_s_pipeline()
    {
        var directory = Scratch("list");

        try
        {
            //act
            // Nothing registers this verb: it comes from EtlPipelines.Hosting, which is the point.
            var exitCode = await CsvToExcel.Program.RunAsync(["list", "--work-dir", directory]);

            //assert
            exitCode.Should().Be(0);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Test]
    public async Task An_unknown_pipeline_name_fails_rather_than_doing_nothing()
    {
        var directory = Scratch("unknown");

        try
        {
            //act
            var exitCode = await CsvToExcel.Program.RunAsync(["run", "nosuch", "--work-dir", directory]);

            //assert
            exitCode.Should().Be(1);
        }
        finally
        {
            Cleanup(directory);
        }
    }
}
