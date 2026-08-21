using System.IO.Abstractions;
using EtlPipelines.Abstractions.Configuration;
using EtlPipelines.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Excel.Tests;

/// <summary>
/// What happens to rows a worksheet cannot be converted into.
/// </summary>
/// <remarks>
/// <para>
/// One unconvertible cell costing a million-row load is as unwelcome here as it is in a CSV, so the
/// source skips such rows by default, counts them, and hands their cell values to a dead-letter sink
/// when one is registered.
/// </para>
/// <para>
/// This is also the behaviour that dictated how the source reads. MiniExcel's typed reader converts
/// inside its iterator, and an iterator that has thrown cannot be resumed — so skipping one bad row
/// there would mean abandoning the rest of the sheet. Reading the untyped stream and converting a row
/// at a time is what makes these tests possible at all.
/// </para>
/// </remarks>
public sealed class ExcelMalformedRowTests
{
    private const string PipelineName = "orders";

    private readonly ExcelTestHost _host = new();

    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    /// <summary>The same shape, but with an amount that need not be a number.</summary>
    public sealed class LooseOrder
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
    }

    /// <summary>Writes a workbook whose 2nd, 5th and 8th data rows have an unconvertible amount.</summary>
    private async Task<IFileInfo> FileWithBadRows()
    {
        var rows = Enumerable.Range(1, 10)
            .Select(i => new LooseOrder
            {
                Id = i,
                Customer = $"customer-{i}",
                Amount = i is 2 or 5 or 8 ? "not-a-number" : $"{i}.50",
            })
            .ToArray();

        // Written straight through the sink rather than through a pipeline: running one here would
        // build the container before the test has registered the pipeline it actually wants to run.
        var file = _host.File("bad.xlsx");
        var sink = new ExcelSink<LooseOrder>(file);

        await sink.InitializeAsync(CancellationToken.None);
        (await sink.WriteAsync(rows, CancellationToken.None)).IsError.Should().BeFalse();
        (await sink.CompleteAsync(CancellationToken.None)).IsError.Should().BeFalse();
        await sink.DisposeAsync();

        return file;
    }

    [Test]
    public async Task Skips_bad_rows_and_keeps_the_good_ones()
    {
        //arrange
        var file = await FileWithBadRows();
        var sink = new CollectingSink<Order>();

        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 4)
            .FromExcel<Order>(file)
            .To(_ => sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse("three bad rows must not cost the other seven");
        sink.Rows.Should().HaveCount(7);
        sink.Rows.Select(o => o.Id).Should().Equal(1, 3, 4, 6, 7, 9, 10);
    }

    [Test]
    public async Task Takes_the_dead_letter_sink_from_the_container()
    {
        //arrange
        // Nothing wires the dead-letter sink to the source explicitly: FromExcel looks for one in the
        // container. Registering it is the whole of the configuration.
        var file = await FileWithBadRows();
        var deadLetters = new RecordingDeadLetterSink<string>();

        _host.Configure(s => s.AddSingleton<IDeadLetterSink<string>>(deadLetters))
             .AddEtlPipeline(PipelineName, b => b.FromExcel<Order>(file).To(_ => new CollectingSink<Order>()));

        //act
        await _host.RunAsync(PipelineName);

        //assert
        // The cell values are what make the row recoverable — the converted shape is exactly what is
        // missing.
        deadLetters.Entries.Should().HaveCount(3);
        deadLetters.Entries.Should().OnlyContain(e => e.Row.Contains("not-a-number"));
        deadLetters.Entries.Select(e => e.Row).Should().Contain(r => r.Contains("customer-5"));
        deadLetters.Entries.Should().OnlyContain(e => e.Error.Code == "excel.cell_not_convertible");
    }

    [Test]
    public async Task Counts_skipped_rows_without_a_dead_letter_sink_registered()
    {
        //arrange
        var file = await FileWithBadRows();
        var source = new ExcelSource<Order>(file);

        _host.AddEtlPipeline(PipelineName, b => b.From<Order>(_ => source).To(_ => new CollectingSink<Order>()));

        //act
        await _host.RunAsync(PipelineName);

        //assert
        source.MalformedRows.Should().Be(3, "skipping is not the same as ignoring");
    }

    [Test]
    public async Task Skipped_rows_do_not_reach_the_runs_failure_count()
    {
        //arrange
        // Pinning a known limitation rather than asserting desired behaviour. A transform rejecting a
        // row flows into RowsFailed and MaxRowErrors; IDataSource.ReadAsync returns only a count, so
        // a source has nowhere to report one. Closing the gap means giving the source port a
        // rejection channel — when that lands, this test should change.
        var file = await FileWithBadRows();
        var source = new ExcelSource<Order>(file);

        _host.AddEtlPipeline(PipelineName, b => b.From<Order>(_ => source).To(_ => new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        source.MalformedRows.Should().Be(3, "the source counts them itself");
        result.Value.RowsFailed.Should().Be(0, "but the source port cannot report them to the run");
    }

    [Test]
    public async Task Fails_the_run_when_told_not_to_skip()
    {
        //arrange
        var file = await FileWithBadRows();

        _host.AddEtlPipeline(PipelineName, b => b
            .FromExcel<Order>(file, new ExcelSourceOptions { SkipMalformedRows = false })
            .To(_ => new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue("a workbook meant to be perfect should stop the run when it is not");
        result.FirstError.Code.Should().Be("excel.cell_not_convertible");
    }

    [Test]
    public async Task Names_the_column_and_the_value_that_would_not_convert()
    {
        //arrange
        // A message naming only the row is what makes a bad workbook slow to diagnose, so the column,
        // the offending value and the target type are all part of it.
        var file = await FileWithBadRows();

        _host.AddEtlPipeline(PipelineName, b => b
            .FromExcel<Order>(file, new ExcelSourceOptions { SkipMalformedRows = false })
            .To(_ => new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.FirstError.Description.Should().Contain("Amount")
            .And.Contain("not-a-number")
            .And.Contain("Decimal");
    }

    [Test]
    public async Task Ignores_columns_the_row_type_does_not_mention()
    {
        //arrange
        // A sheet routinely carries more than one pipeline cares about, so an unknown column is not an
        // error — the row is built from the columns that do match.
        var file = await FileWithBadRows();
        var sink = new CollectingSink<JustTheId>();

        _host.AddEtlPipeline(PipelineName, b => b.FromExcel<JustTheId>(file).To(_ => sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().HaveCount(10, "the unconvertible Amount column is not read at all now");
        sink.Rows.Select(r => r.Id).Should().Equal(Enumerable.Range(1, 10));
    }

    [Test]
    public async Task Reports_a_clear_error_when_read_before_initialization()
    {
        //arrange
        var file = await FileWithBadRows();
        var source = new ExcelSource<Order>(file);

        //act
        var read = await source.ReadAsync(new Order[1], CancellationToken.None);

        //assert
        read.IsError.Should().BeTrue();
        read.FirstError.Code.Should().Be("excel.not_initialized");
    }

    public sealed class JustTheId
    {
        public int Id { get; set; }
    }
}
