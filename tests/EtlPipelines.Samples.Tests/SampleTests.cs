using System.IO.Abstractions;
using EtlPipelines.Samples.Branching;
using EtlPipelines.Samples.CsvToDatabase;
using EtlPipelines.Samples.CsvToExcel;
using EtlPipelines.Samples.ExcelToSql;
using EtlPipelines.Samples.SqlToWorkbook;
using Microsoft.Extensions.DependencyInjection;
using MiniExcelLib;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Samples.Tests;

/// <summary>
/// Every sample, run end to end, checked against what it claims to produce.
/// </summary>
/// <remarks>
/// Samples are documentation that can rot without anybody noticing, because nothing compiles against
/// their behaviour. Each is exercised twice here: once through its registration, so the row counts
/// can be asserted on, and once through <c>Cli.RunAsync</c> — the same entry point a shell reaches —
/// so the command wiring is covered too rather than only the pipeline underneath it.
/// </remarks>
[Category("Samples")]
public class SampleTests
{
    [Test]
    public async Task Csv_to_excel_filters_and_reshapes_into_a_workbook()
    {
        //arrange
        await using var scratch = new SampleScratch("csv-excel", (services, workspace) =>
            services.AddSingleton<SalesData>().AddSalesPipeline(workspace));

        await scratch.GetRequiredService<SalesData>().WriteAsync(CancellationToken.None);

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // 500 rows in, every tenth a refund the pipeline drops.
        result.Value.RowsRead.Should().Be(500);
        result.Value.RowsWritten.Should().Be(450);

        var workbook = scratch.File(SalesPipeline.OutputFile);
        workbook.Refresh();
        workbook.Exists.Should().BeTrue();

        var rows = MiniExcel.Importers.GetOpenXmlImporter()
            .Query<SalesReport>(workbook.OpenRead())
            .ToList();

        rows.Should().HaveCount(450);
        rows.Should().OnlyContain(r => r.AmountInCents > 0, "the refunds were filtered out");
        rows[0].AmountInCents.Should().Be(125m, "1.25 became cents");
    }

    [Test]
    public async Task Sql_to_workbook_writes_one_sheet_per_query()
    {
        //arrange
        await using var scratch = new SampleScratch("sql-workbook", (services, workspace) =>
            services.AddSingleton<ReportData>().AddReportPipeline(workspace));

        await scratch.GetRequiredService<ReportData>().SeedAsync(CancellationToken.None);

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.Stages.Should().HaveCount(3, "one stage per sheet");

        var report = scratch.File(ReportPipeline.OutputFile);
        report.Refresh();
        report.Exists.Should().BeTrue();

        var importer = MiniExcel.Importers.GetOpenXmlImporter();
        importer.GetSheetNames(report.OpenRead())
            .Should().Equal(["Orders", "By region", "Customers"], "sheets keep the order they were declared");

        importer.Query<Order>(report.OpenRead(), sheetName: "Orders").Should().HaveCount(ReportData.Rows);
        importer.Query<Customer>(report.OpenRead(), sheetName: "Customers").Should().HaveCount(ReportData.Rows);
    }

    [Test]
    public async Task Excel_to_sql_loads_what_it_can_and_sets_the_rest_aside()
    {
        //arrange
        await using var scratch = new SampleScratch("excel-sql", (services, workspace) =>
            services.AddSingleton<ImportData>().AddImportPipeline(workspace));

        var data = scratch.GetRequiredService<ImportData>();
        await data.PrepareAsync(CancellationToken.None);

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // 200 rows, every fortieth unreadable; the rest reach the table inside one transaction.
        result.Value.RowsRead.Should().Be(195);
        result.Value.RowsWritten.Should().Be(195);
        (await data.CountAsync(CancellationToken.None)).Should().Be(195);

        scratch.GetRequiredService<RejectedRows>().Rows
            .Should().HaveCount(5, "the rows that were not numbers went to the dead-letter sink");
    }

    [Test]
    public async Task Branching_reads_once_and_writes_to_both_destinations()
    {
        //arrange
        await using var scratch = new SampleScratch("branching", (services, workspace) =>
            services.AddSingleton<ReadingsData>().AddReadingsPipeline(workspace));

        await scratch.GetRequiredService<ReadingsData>().WriteAsync(CancellationToken.None);

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(ReadingsData.Rows, "the source is read once however many branches there are");

        var archive = scratch.File(ReadingsPipeline.ArchiveFile);
        var report = scratch.File(ReadingsPipeline.ReportFile);
        archive.Refresh();
        report.Refresh();
        archive.Exists.Should().BeTrue();
        report.Exists.Should().BeTrue();

        // The archive keeps every row; the report only the warm ones, so it is strictly smaller.
        var archived = (await archive.ReadAllLinesAsync(CancellationToken.None)).Length - 1;
        var reported = MiniExcel.Importers.GetOpenXmlImporter()
            .Query<ReadingReport>(report.OpenRead()).Count();

        archived.Should().Be(ReadingsData.Rows);
        reported.Should().BeLessThan(archived).And.BeGreaterThan(0);
        result.Value.RowsWritten.Should().Be(archived + reported);
    }

    [Test]
    public async Task Csv_to_database_loads_a_file_into_sqlite()
    {
        //arrange
        await using var scratch = new SampleScratch("csv-sqlite", (services, workspace) =>
            services.AddSingleton<TradesData>().AddTradesPipeline(workspace));

        var data = scratch.GetRequiredService<TradesData>();
        await data.WriteAsync(CancellationToken.None);
        await data.CreateSqliteTableAsync(CancellationToken.None);

        //act
        var result = await scratch.RunAsync();

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.RowsRead.Should().Be(TradesData.Rows);
        result.Value.RowsWritten.Should().Be(TradesPipeline.ExpectedRows);
    }
}
