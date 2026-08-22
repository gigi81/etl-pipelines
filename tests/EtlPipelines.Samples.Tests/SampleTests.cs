using System.IO.Abstractions;
using MiniExcelLib;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Samples.Tests;

/// <summary>
/// Every sample, run end to end, checked against what it claims to produce.
/// </summary>
/// <remarks>
/// Samples are documentation that can rot without anybody noticing, because nothing compiles against
/// their behaviour. Running them here and asserting on the files they leave behind is what keeps the
/// README's claims and the code in step.
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
        var result = await CsvToExcel.Sample.RunAsync(scratch.Directory);

        //assert
        // 500 rows in, every tenth a refund the pipeline drops.
        result.RowsRead.Should().Be(500);
        result.RowsWritten.Should().Be(450);

        var workbook = scratch.File("sales.xlsx");
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
        var result = await SqlToWorkbook.Sample.RunAsync(scratch.Directory);

        //assert
        result.Stages.Should().HaveCount(3, "one stage per sheet");

        var report = scratch.File("report.xlsx");
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
        var (result, rejected, loaded) = await ExcelToSql.Sample.RunAsync(scratch.Directory);

        //assert
        // 200 rows, every fortieth unreadable; the rest reach the table inside one transaction.
        rejected.Should().Be(5, "the rows that were not numbers went to the dead-letter sink");
        result.RowsRead.Should().Be(195);
        loaded.Should().Be(195);
    }

    [Test]
    public async Task Branching_reads_once_and_writes_to_both_destinations()
    {
        //arrange
        using var scratch = new SampleScratch("branching");

        //act
        var result = await Branching.Sample.RunAsync(scratch.Directory);

        //assert
        result.RowsRead.Should().Be(2_000, "the source is read once however many branches there are");

        var archive = scratch.File("archive.csv");
        var report = scratch.File("report.xlsx");
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
        result.RowsWritten.Should().Be(archived + reported);
    }

    [Test]
    public async Task Csv_to_database_loads_a_file_into_sqlite()
    {
        //arrange
        using var scratch = new SampleScratch("csv-sqlite");
        var connectionString = $"Data Source={scratch.File("trades.db").FullName}";

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"CREATE TABLE {CsvToDatabase.Sample.TableName} (Id INTEGER, Symbol TEXT, Price NUMERIC, Quantity INTEGER)";
            await command.ExecuteNonQueryAsync();
        }

        //act
        var result = await CsvToDatabase.Sample.RunAsync(
            scratch.Directory,
            EtlPipelines.Sql.Sqlite.SqliteConnections.Open(connectionString));

        //assert
        result.RowsRead.Should().Be(10_000);
        result.RowsWritten.Should().Be(CsvToDatabase.Sample.ExpectedRows);
    }
}
