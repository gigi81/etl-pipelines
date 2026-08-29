using EtlPipelines.Extensions.Excel;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Extensions.Excel.Tests;

/// <summary>
/// Several sources converging into one workbook, a sheet each.
/// </summary>
/// <remarks>
/// The pipeline model already had the shape this needs: <c>To(...)</c> hands the pipeline builder
/// back, so each <c>From(...).ToExcelSheet(...)</c> is its own stage, and stages run one after
/// another. What the connector adds is a workbook shared across those stages for the length of a run,
/// and a promotion that happens once, after the last sheet.
/// </remarks>
public sealed class ExcelMultiSheetTests
{
    private const string PipelineName = "report";

    private readonly ExcelTestHost _host = new();

    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public sealed class Customer
    {
        public string Name { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
    }

    private static Order[] Orders(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new Order { Id = i, Customer = $"c{i}", Amount = i * 1.5m })];

    private static Customer[] Customers(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new Customer { Name = $"c{i}", Country = i % 2 == 0 ? "IT" : "UK" })];

    [Test]
    public async Task Writes_two_sources_into_one_workbook_as_separate_sheets()
    {
        //arrange
        var report = _host.File("report.xlsx");
        var readOrders = new CollectingSink<Order>();
        var readCustomers = new CollectingSink<Customer>();

        _host.AddEtlPipeline(PipelineName, b => b
                 .From(new ArraySource<Order>(Orders(50)))
                 .ToExcelSheet(report, "Orders")
                 .From(new ArraySource<Customer>(Customers(20)))
                 .ToExcelSheet(report, "Customers"))
             .AddEtlPipeline("read-orders", b => b
                 .FromExcel<Order>(report, new ExcelSourceOptions { SheetName = "Orders" })
                 .To(_ => readOrders))
             .AddEtlPipeline("read-customers", b => b
                 .FromExcel<Customer>(report, new ExcelSourceOptions { SheetName = "Customers" })
                 .To(_ => readCustomers));

        //act
        var result = await _host.RunAsync(PipelineName);
        await _host.RunAsync("read-orders");
        await _host.RunAsync("read-customers");

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        // Two stages, each reporting its own rows, into one file.
        result.Value.Stages.Should().HaveCount(2);
        result.Value.RowsWritten.Should().Be(20, "RowsWritten is the last stage's output");

        readOrders.Rows.Should().HaveCount(50);
        readCustomers.Rows.Should().HaveCount(20);
        readCustomers.Rows[1].Country.Should().Be("IT");
    }

    [Test]
    public async Task Keeps_the_sheets_in_the_order_they_were_declared()
    {
        //arrange
        var report = _host.File("ordered.xlsx");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>(Orders(2))).ToExcelSheet(report, "First")
            .From(new ArraySource<Order>(Orders(2))).ToExcelSheet(report, "Second")
            .From(new ArraySource<Order>(Orders(2))).ToExcelSheet(report, "Third"));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        var names = MiniExcelLib.MiniExcel.Importers.GetOpenXmlImporter()
            .GetSheetNames(report.OpenRead());
        names.Should().Equal("First", "Second", "Third");
    }

    [Test]
    public async Task The_workbook_appears_only_once_every_sheet_is_written()
    {
        //arrange
        // The whole reason the promotion waits: a workbook holding the first sheet but not the second
        // is not a partial file a reader can cope with, it is a wrong one.
        var report = _host.File("atomic.xlsx");
        var seenAfterFirstStage = false;

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>(Orders(10)))
            .ToExcelSheet(report, "Orders")
            .AddStage("look", (_, _) =>
            {
                report.Refresh();
                seenAfterFirstStage = report.Exists;
                return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
            })
            .From(new ArraySource<Customer>(Customers(5)))
            .ToExcelSheet(report, "Customers"));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        seenAfterFirstStage.Should().BeFalse("the target must not appear while sheets are still coming");

        report.Refresh();
        report.Exists.Should().BeTrue();
        _host.TempFiles().Should().BeEmpty("the temporary file is renamed, not left behind");
    }

    [Test]
    public async Task A_failure_on_a_later_sheet_leaves_no_workbook_at_all()
    {
        //arrange
        var report = _host.File("failed.xlsx");

        // Enough rows, and a small enough batch, that the sibling fails while the sheet is still
        // being written. A single-batch source would let the sheet finish and commit before the
        // failure was recorded, which is a wider runtime behaviour rather than anything about sheets.
        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 8)
            .From(new ArraySource<Order>(Orders(10)))
            .ToExcelSheet(report, "Orders")
            .From(new ArraySource<Customer>(Customers(400)))
            .Branch(
                b1 => b1.ToExcelSheet(report, "Customers"),
                b2 => b2.To(new FailingSink<Customer>(failAfter: 40, TimeSpan.FromMilliseconds(10)))));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();

        report.Refresh();
        report.Exists.Should().BeFalse("a workbook missing a sheet must never reach the target path");
        _host.TempFiles().Should().ContainSingle("the partial workbook is kept for inspection");
    }

    [Test]
    public async Task Refuses_two_sheets_of_one_workbook_written_at_the_same_time()
    {
        //arrange
        // Branch legs run concurrently, and two writers interleaving into one zip archive produce a
        // file that will not open. Saying so beats producing it.
        var report = _host.File("branched.xlsx");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>(Orders(50)))
            .Branch(
                b1 => b1.ToExcelSheet(report, "One"),
                b2 => b2.ToExcelSheet(report, "Two")));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("one after another");

        report.Refresh();
        report.Exists.Should().BeFalse();
    }

    [Test]
    public void Refuses_two_sheets_with_the_same_name()
    {
        //arrange
        var report = _host.File("dupe.xlsx");

        //act
        var act = () => _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>(Orders(1))).ToExcelSheet(report, "Same")
            .From(new ArraySource<Order>(Orders(1))).ToExcelSheet(report, "Same"));

        //assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*already has a sheet named 'Same'*");
    }

    [Test]
    public async Task Runs_the_same_pipeline_twice_without_carrying_sheets_over()
    {
        //arrange
        // The workbook is keyed on the run's own scope, so a second run builds its own file rather
        // than inserting its sheets into the one the first run left.
        var report = _host.File("twice.xlsx");

        _host.AddEtlPipeline(PipelineName, b => b
            .From(new ArraySource<Order>(Orders(3))).ToExcelSheet(report, "Orders")
            .From(new ArraySource<Customer>(Customers(2))).ToExcelSheet(report, "Customers"));

        //act
        var first = await _host.RunAsync(PipelineName);
        var second = await _host.RunAsync(PipelineName);

        //assert
        first.IsError.Should().BeFalse(first.IsError ? first.FirstError.Description : null);
        second.IsError.Should().BeFalse(second.IsError ? second.FirstError.Description : null);

        var names = MiniExcelLib.MiniExcel.Importers.GetOpenXmlImporter()
            .GetSheetNames(report.OpenRead());
        names.Should().Equal(
            ["Orders", "Customers"],
            "the second run replaced the workbook, not appended to it");
        _host.TempFiles().Should().BeEmpty();
    }
}
