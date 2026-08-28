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
    public async Task Archive_to_database_runs_both_pipelines_in_order_when_none_is_named()
    {
        var directory = Scratch("archive-sqlite");

        try
        {
            // No name, so this runs every registered pipeline in registration order - build-feed,
            // which the real job depends on, then archive. Naming just "archive" here would fail,
            // which is the point of registering the two separately.
            var exitCode = await ArchiveToDatabase.Program.RunAsync(["run", "--work-dir", directory]);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, ArchiveToDatabase.Pipeline.ArchiveFile)).Should().BeTrue();
            File.Exists(Path.Combine(directory, "archive.db")).Should().BeTrue();
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Test]
    public async Task Archive_to_database_job_alone_fails_before_the_feed_exists()
    {
        var directory = Scratch("archive-job-only");

        try
        {
            //act
            var exitCode = await ArchiveToDatabase.Program.RunAsync(["run", ArchiveToDatabase.Pipeline.Name, "--work-dir", directory]);

            //assert
            exitCode.Should().Be(1, "the real job never builds its own input");
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

    [Test]
    public async Task Run_without_a_name_runs_every_registered_pipeline()
    {
        var directory = Scratch("run-all");

        try
        {
            //act
            // No pipeline named, so the run verb takes that as all of them.
            var exitCode = await CsvToExcel.Program.RunAsync(["run", "--work-dir", directory]);

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
    public async Task No_arguments_at_all_runs_every_registered_pipeline()
    {
        // Nothing to point --work-dir at, because the whole point is passing no arguments: the
        // sample falls back to a directory of its own under the temp path, which is found and
        // removed afterwards rather than left behind.
        var before = Directory.GetDirectories(Path.GetTempPath(), "etl-workspace-sales-*");

        try
        {
            //act
            var exitCode = await CsvToExcel.Program.RunAsync([]);

            //assert
            exitCode.Should().Be(0, "an application asked for nothing in particular runs its pipelines");
        }
        finally
        {
            foreach (var left in Directory.GetDirectories(Path.GetTempPath(), "etl-workspace-sales-*").Except(before))
            {
                Cleanup(left);
            }
        }
    }

    [Test]
    public async Task Verbose_is_accepted_wherever_it_is_put()
    {
        var directory = Scratch("verbose");

        try
        {
            //act
            // Recursive on the root command, so it parses before the verb as readily as after it.
            var trailing = await CsvToExcel.Program.RunAsync(
                ["run", CsvToExcel.Pipeline.Name, "--work-dir", directory, "--verbose"]);
            var leading = await CsvToExcel.Program.RunAsync(
                ["-v", "run", CsvToExcel.Pipeline.Name, "--work-dir", directory]);

            //assert
            trailing.Should().Be(0);
            leading.Should().Be(0);
        }
        finally
        {
            Cleanup(directory);
        }
    }
}
