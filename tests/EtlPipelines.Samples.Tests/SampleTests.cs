using System.IO.Abstractions;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.DependencyInjection;
using MiniExcelLib;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Samples.Tests;

/// <summary>
/// Every sample, run end to end, checked against what it claims to produce.
/// </summary>
/// <remarks>
/// Samples are documentation that can rot without anybody noticing, because nothing compiles against
/// their behaviour. Running them here and asserting on the files they leave behind is what keeps the
/// README's claims and the code in step. They are run through <see cref="SampleHost"/>, the same host
/// their <c>Program.cs</c> starts, so what passes here is true of the sample as somebody would run it.
/// </remarks>
[Category("Samples")]
public class SampleTests
{
    [Test]
    public async Task Csv_to_excel_filters_and_reshapes_into_a_workbook()
    {
        //arrange
        using var scratch = new SampleScratch("csv-excel");

        //act
        await using var run = await SampleHost.RunAsync(new CsvToExcel.CsvToExcelSample(), scratch.Workspace);

        //assert
        // 500 rows in, every tenth a refund the pipeline drops.
        run.Result.RowsRead.Should().Be(500);
        run.Result.RowsWritten.Should().Be(450);

        var workbook = run.File("sales.xlsx");
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
        using var scratch = new SampleScratch("sql-workbook");

        //act
        await using var run = await SampleHost.RunAsync(new SqlToWorkbook.SqlToWorkbookSample(), scratch.Workspace);

        //assert
        run.Result.Stages.Should().HaveCount(3, "one stage per sheet");

        var report = run.File("report.xlsx");
        report.Refresh();
        report.Exists.Should().BeTrue();

        var importer = MiniExcel.Importers.GetOpenXmlImporter();
        importer.GetSheetNames(report.OpenRead())
            .Should().Equal(["Orders", "By region", "Customers"], "sheets keep the order they were declared");

        importer.Query<SqlToWorkbook.Order>(report.OpenRead(), sheetName: "Orders")
            .Should().HaveCount(200);
        importer.Query<SqlToWorkbook.Customer>(report.OpenRead(), sheetName: "Customers")
            .Should().HaveCount(200);
    }

    [Test]
    public async Task Excel_to_sql_loads_what_it_can_and_sets_the_rest_aside()
    {
        //arrange
        using var scratch = new SampleScratch("excel-sql");

        //act
        await using var run = await SampleHost.RunAsync(new ExcelToSql.ExcelToSqlSample(), scratch.Workspace);

        //assert
        // 200 rows, every fortieth unreadable; the rest reach the table inside one transaction.
        // The dead-letter sink is resolved from the host the sample ran in, which is the only place
        // that knows what the run set aside.
        run.Services.GetRequiredService<ExcelToSql.RejectedRows>().Rows
            .Should().HaveCount(5, "the rows that were not numbers went to the dead-letter sink");
        run.Result.RowsRead.Should().Be(195);
        run.Result.RowsWritten.Should().Be(195);
    }

    [Test]
    public async Task Branching_reads_once_and_writes_to_both_destinations()
    {
        //arrange
        using var scratch = new SampleScratch("branching");

        //act
        await using var run = await SampleHost.RunAsync(new Branching.BranchingSample(), scratch.Workspace);

        //assert
        run.Result.RowsRead.Should().Be(2_000, "the source is read once however many branches there are");

        var archive = run.File("archive.csv");
        var report = run.File("report.xlsx");
        archive.Refresh();
        report.Refresh();
        archive.Exists.Should().BeTrue();
        report.Exists.Should().BeTrue();

        // The archive keeps every row; the report only the warm ones, so it is strictly smaller.
        var archived = (await archive.ReadAllLinesAsync(CancellationToken.None)).Length - 1;
        var reported = MiniExcel.Importers.GetOpenXmlImporter()
            .Query<Branching.ReadingReport>(report.OpenRead()).Count();

        archived.Should().Be(2_000);
        reported.Should().BeLessThan(archived).And.BeGreaterThan(0);
        run.Result.RowsWritten.Should().Be(archived + reported);
    }

    [Test]
    public async Task Csv_to_database_loads_a_file_into_sqlite()
    {
        //arrange
        using var scratch = new SampleScratch("csv-sqlite");

        //act
        // No connection supplied, so the sample brings its own SQLite file and creates the table.
        await using var run = await SampleHost.RunAsync(
            new CsvToDatabase.CsvToDatabaseSample(), scratch.Workspace);

        //assert
        run.Result.RowsRead.Should().Be(10_000);
        run.Result.RowsWritten.Should().Be(CsvToDatabase.CsvToDatabaseSample.ExpectedRows);
    }
}
