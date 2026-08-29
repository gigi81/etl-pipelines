using System.IO.Abstractions;
using EtlPipelines.Samples.ArchiveToDatabase.Stages;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using MiniExcelLib;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Samples.Tests;

/// <summary>
/// Every sample, run end to end, checked against what it claims to produce.
/// </summary>
/// <remarks>
/// Samples are documentation that can rot without anybody noticing, because nothing compiles against
/// their behaviour. Each is exercised twice: once through its registration, here, so the row counts
/// can be asserted on, and once through <c>Command.RunAsync</c> — the same entry point a shell
/// reaches — so the wiring is covered too rather than only the pipeline underneath it.
/// <para>
/// Nothing seeds anything first. Putting the input in place is the pipeline's own opening stage, so
/// running the pipeline is the whole of the arrangement.
/// </para>
/// </remarks>
[Category("Samples")]
public class SampleTests
{
    [Test]
    public async Task Csv_to_excel_filters_and_reshapes_into_a_workbook()
    {
        //arrange
        await using var scratch = new SampleScratch("csv-excel", (services, directory) =>
            CsvToExcel.Pipeline.AddPipeline(services, directory));

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // 500 rows in, every tenth a refund the pipeline drops.
        result.Value.RowsRead.Should().Be(500);
        result.Value.RowsWritten.Should().Be(450);

        var workbook = scratch.File(CsvToExcel.Pipeline.OutputFile);
        workbook.Refresh();
        workbook.Exists.Should().BeTrue();

        var rows = MiniExcel.Importers.GetOpenXmlImporter()
            .Query<CsvToExcel.SalesReport>(workbook.OpenRead())
            .ToList();

        rows.Should().HaveCount(450);
        rows.Should().OnlyContain(r => r.AmountInCents > 0, "the refunds were filtered out");
        rows[0].AmountInCents.Should().Be(125m, "1.25 became cents");
    }

    [Test]
    public async Task Sql_to_workbook_writes_one_sheet_per_query()
    {
        //arrange
        await using var scratch = new SampleScratch("sql-workbook", (services, directory) =>
            SqlToWorkbook.Pipeline.AddPipeline(services, directory));

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.Stages.Should().HaveCount(4, "the seed step, then one stage per sheet");
        result.Value.Stages[0].Name.Should().Be("seed", "the database is filled before the queries run");

        var report = scratch.File(SqlToWorkbook.Pipeline.OutputFile);
        report.Refresh();
        report.Exists.Should().BeTrue();

        var importer = MiniExcel.Importers.GetOpenXmlImporter();
        (await importer.GetSheetNamesAsync(report.OpenRead()))
            .Should().Equal(["Orders", "By region", "Customers"], "sheets keep the order they were declared");

        importer.Query<SqlToWorkbook.Order>(report.OpenRead(), sheetName: "Orders").Should().HaveCount(SqlToWorkbook.Stages.SeedStage.Rows);
        importer.Query<SqlToWorkbook.Customer>(report.OpenRead(), sheetName: "Customers").Should().HaveCount(SqlToWorkbook.Stages.SeedStage.Rows);
    }

    [Test]
    public async Task Excel_to_sql_loads_what_it_can_and_sets_the_rest_aside()
    {
        //arrange
        await using var scratch = new SampleScratch("excel-sql", (services, directory) =>
            ExcelToSql.Pipeline.AddPipeline(services, directory));

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // 200 rows, every fortieth unreadable; the rest reach the table inside one transaction.
        result.Value.RowsRead.Should().Be(195);
        result.Value.RowsWritten.Should().Be(195);
        (await ExcelToSql.Pipeline.CountAsync(scratch.Directory, CancellationToken.None))
            .Should().Be(195);

        scratch.GetRequiredService<ExcelToSql.RejectedRows>().Rows
            .Should().HaveCount(5, "the rows that were not numbers went to the dead-letter sink");
    }

    [Test]
    public async Task Branching_reads_once_and_writes_to_both_destinations()
    {
        //arrange
        await using var scratch = new SampleScratch("branching", (services, directory) =>
            Branching.Pipeline.AddPipeline(services, directory));

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(Branching.Stages.SeedStage.Rows, "the source is read once however many branches there are");

        var archive = scratch.File(Branching.Pipeline.ArchiveFile);
        var report = scratch.File(Branching.Pipeline.ReportFile);
        archive.Refresh();
        report.Refresh();
        archive.Exists.Should().BeTrue();
        report.Exists.Should().BeTrue();

        // The archive keeps every row; the report only the warm ones, so it is strictly smaller.
        var archived = (await archive.ReadAllLinesAsync(CancellationToken.None)).Length - 1;
        var reported = MiniExcel.Importers.GetOpenXmlImporter()
            .Query<Branching.ReadingReport>(report.OpenRead()).Count();

        archived.Should().Be(Branching.Stages.SeedStage.Rows);
        reported.Should().BeLessThan(archived).And.BeGreaterThan(0);
        result.Value.RowsWritten.Should().Be(archived + reported);
    }

    [Test]
    public async Task Csv_to_database_loads_a_file_into_sqlite()
    {
        //arrange
        await using var scratch = new SampleScratch("csv-sqlite", (services, directory) =>
        {
            services.AddSqliteConnection(
                CsvToDatabase.Pipeline.Connection,
                CsvToDatabase.Pipeline.ConnectionString(directory));
            CsvToDatabase.Pipeline.AddPipeline(services, directory);
        });

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(CsvToDatabase.Stages.SeedStage.Rows);
        result.Value.RowsWritten.Should().Be(CsvToDatabase.Pipeline.ExpectedRows);
    }

    [Test]
    public async Task Archive_to_database_builds_the_feed_separately_from_the_job_that_reads_it()
    {
        //arrange
        await using var scratch = new SampleScratch("archive-sqlite", (services, directory) =>
        {
            services.AddSqliteConnection(
                ArchiveToDatabase.Pipeline.Connection,
                ArchiveToDatabase.Pipeline.ConnectionString(directory));
            ArchiveToDatabase.Pipeline.AddPipeline(services, directory);
        });

        var archive = scratch.File(ArchiveToDatabase.Pipeline.ArchiveFile);

        //act
        var seeded = await scratch.RunAsync(ArchiveToDatabase.Pipeline.SeedName);

        //assert - the setup pipeline alone produces the feed, and nothing else
        seeded.IsError.Should().BeFalse(seeded.IsError ? seeded.FirstError.Description : null);
        seeded.Value.Stages.Should().HaveCount(2, "writing the files and bundling them are its only two stages");
        archive.Refresh();
        archive.Exists.Should().BeTrue("build-feed bundles the five files into a real zip");

        foreach (var table in ArchiveToDatabase.Pipeline.Tables)
        {
            scratch.Directory.SubDirectory(ArchiveToDatabase.Pipeline.ExtractedDirectory)
                .File($"{table}.csv").Exists.Should().BeFalse("the real job has not run yet");
        }

        //act
        var loaded = await scratch.RunAsync(ArchiveToDatabase.Pipeline.Name);

        //assert - the real job only extracts and loads; it never wrote the feed itself
        loaded.IsError.Should().BeFalse(loaded.IsError ? loaded.FirstError.Description : null);
        loaded.Value.Stages.Should().HaveCount(
            3,
            "extract, create-tables, then one parallel block truncating and loading every table");
        loaded.Value.Stages.Select(s => s.Name).Should().NotContain(
            s => s.Contains("compress"), "compressing the feed belongs to build-feed, not to this pipeline");
        loaded.Value.Stages.Select(s => s.Name).Should().Contain(
            $"Parallel({ArchiveToDatabase.Pipeline.Tables.Length})",
            "the five truncate-and-load chains run as one parallel block, one branch per table");

        foreach (var table in ArchiveToDatabase.Pipeline.Tables)
        {
            var extracted = scratch.Directory
                .SubDirectory(ArchiveToDatabase.Pipeline.ExtractedDirectory)
                .File($"{table}.csv");
            extracted.Refresh();
            extracted.Exists.Should().BeTrue($"{table}.csv should have come back out of the archive");
        }

        await using var connection = new SqliteConnection(
            ArchiveToDatabase.Pipeline.ConnectionString(scratch.Directory));
        await connection.OpenAsync();

        foreach (var table in ArchiveToDatabase.Pipeline.Tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            var count = (long)(await command.ExecuteScalarAsync())!;
            count.Should().Be(SeedStage.RowsPerFile, $"{table} should hold every row its CSV carried");
        }
    }

    [Test]
    public async Task Archive_to_database_job_fails_when_the_feed_was_never_built()
    {
        //arrange
        await using var scratch = new SampleScratch("archive-no-feed", (services, directory) =>
        {
            services.AddSqliteConnection(
                ArchiveToDatabase.Pipeline.Connection,
                ArchiveToDatabase.Pipeline.ConnectionString(directory));
            ArchiveToDatabase.Pipeline.AddPipeline(services, directory);
        });

        //act
        // The real job never builds its own input - running it before build-feed is exactly the
        // mistake this separation is meant to make obvious rather than silently paper over.
        var result = await scratch.RunAsync(ArchiveToDatabase.Pipeline.Name);

        //assert
        result.IsError.Should().BeTrue();
    }
}
