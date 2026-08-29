using System.IO.Abstractions;
using EtlPipelines.Excel;

// ReSharper disable once CheckNamespace
namespace EtlPipelines;

/// <summary>Shorthand for putting an Excel workbook at either end of a dataflow.</summary>
/// <remarks>
/// Files are named as <see cref="IFileInfo"/> rather than as paths, because an
/// <see cref="IFileInfo"/> already carries the filesystem it belongs to. That keeps the connector from
/// ever having to construct one — it has no dependency on a concrete filesystem at all — and it means
/// a test passes <c>mockFileSystem.FileInfo.New("orders.xlsx")</c> and everything downstream follows.
/// </remarks>
public static class Extensions
{
    /// <summary>Begins a dataflow reading from a worksheet.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="file">The workbook to read.</param>
    /// <param name="options">Which sheet to read and how to treat rows that will not convert.</param>
    /// <remarks>
    /// Registered through the factory overload rather than as an instance, so each run builds its own
    /// source. A shared instance would carry an open file handle and its read position from one run
    /// into the next, and the second run would find itself already at the end of the sheet.
    /// </remarks>
    public static IDataflowBuilder<TRow> FromExcel<TRow>(
        this IPipelineBuilder builder,
        IFileInfo file,
        ExcelSourceOptions? options = null)
        where TRow : class, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.From(services => new ExcelSource<TRow>(
            file,
            options,
            // Optional: rows that will not convert are recoverable when a dead-letter sink is
            // registered, and merely counted when one is not.
            services.GetService(typeof(IDeadLetterSink<string>)) as IDeadLetterSink<string>));
    }

    /// <summary>Terminates a dataflow by writing to a worksheet.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="file">The destination workbook.</param>
    /// <param name="options">Sheet settings, including whether to write atomically.</param>
    public static IPipelineBuilder ToExcel<TRow>(
        this IDataflowBuilder<TRow> builder,
        IFileInfo file,
        ExcelSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.To(_ => new ExcelSink<TRow>(file, options));
    }

    /// <summary>Terminates a dataflow by writing one sheet of a multi-sheet workbook.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="file">The destination workbook. Pass the same file object for every sheet.</param>
    /// <param name="sheetName">The name of the sheet these rows fill.</param>
    /// <param name="options">Sheet settings, including whether to write atomically.</param>
    /// <remarks>
    /// <para>
    /// Several sources converge on one workbook by giving each its own stage, because
    /// <c>To(...)</c> hands the pipeline builder back:
    /// </para>
    /// <code>
    /// services.AddEtlPipeline("report", b => b
    ///     .From&lt;Orders, OrderRow&gt;().ToExcelSheet(report, "Orders")
    ///     .From&lt;Customers, CustomerRow&gt;().ToExcelSheet(report, "Customers"));
    /// </code>
    /// <para>
    /// Stages run one after another, so the sheets are written in the order they are declared and the
    /// workbook is renamed into place only after the last one. Sheets of one workbook must not be put
    /// in separate branches: branches run at the same time, and two writers interleaving into one zip
    /// archive produce a file that will not open — starting a second sheet while one is still open
    /// throws rather than corrupting it.
    /// </para>
    /// </remarks>
    public static IPipelineBuilder ToExcelSheet<TRow>(
        this IDataflowBuilder<TRow> builder,
        IFileInfo file,
        string sheetName,
        ExcelSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);

        // Counted now, while the pipeline is being composed: the run needs to know how many sheets to
        // expect so it can tell which one is the last.
        SheetPlans.For(file).Add(sheetName);

        var settings = options ?? new ExcelSinkOptions();
        return builder.To(services => new ExcelSheetSink<TRow>(services, file, sheetName, settings));
    }
}
