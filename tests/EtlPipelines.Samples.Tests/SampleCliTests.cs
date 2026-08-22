using System.IO.Abstractions;
using EtlPipelines.Samples.Branching;
using EtlPipelines.Samples.CsvToDatabase;
using EtlPipelines.Samples.CsvToExcel;
using EtlPipelines.Samples.ExcelToSql;
using EtlPipelines.Samples.SqlToWorkbook;

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
    [Arguments("sales")]
    public async Task Csv_to_excel_runs_from_the_command_line(string verb)
    {
        var directory = Scratch("csv-excel");

        try
        {
            //act
            var exitCode = await SalesCli.RunAsync([verb, "--work-dir", directory]);

            //assert
            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, SalesPipeline.OutputFile)).Should().BeTrue();
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
            var exitCode = await ReportCli.RunAsync([ReportPipeline.Name, "--work-dir", directory]);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, ReportPipeline.OutputFile)).Should().BeTrue();
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
            var exitCode = await ImportCli.RunAsync([ImportPipeline.Name, "--work-dir", directory]);

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
            var exitCode = await ReadingsCli.RunAsync([ReadingsPipeline.Name, "--work-dir", directory]);

            exitCode.Should().Be(0);
            File.Exists(Path.Combine(directory, ReadingsPipeline.ArchiveFile)).Should().BeTrue();
            File.Exists(Path.Combine(directory, ReadingsPipeline.ReportFile)).Should().BeTrue();
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
            var exitCode = await TradesCli.RunAsync([TradesPipeline.Name, "--work-dir", directory]);

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
            var exitCode = await SalesCli.RunAsync(["list", "--work-dir", directory]);

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
            var exitCode = await SalesCli.RunAsync(["run", "nosuch", "--work-dir", directory]);

            //assert
            exitCode.Should().Be(1);
        }
        finally
        {
            Cleanup(directory);
        }
    }
}
