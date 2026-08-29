using System.Globalization;
using MiniExcelLib.OpenXml;

namespace EtlPipelines.Excel;

/// <summary>Settings shared by the Excel source and sink.</summary>
public abstract class ExcelOptions
{
    /// <summary>
    /// The culture used to convert cell values. Defaults to
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    /// <remarks>
    /// Excel stores numbers and dates as numbers rather than as text, so this matters less here than
    /// it does for CSV — but it still governs every value that arrives as text and has to be converted,
    /// and a workbook must mean the same thing wherever it is processed.
    /// </remarks>
    public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// The worksheet to use. Defaults to <see langword="null"/>, meaning the first sheet when
    /// reading and a sheet named <c>Sheet1</c> when writing.
    /// </summary>
    public string? SheetName { get; set; }

    /// <summary>
    /// Escape hatch for anything not surfaced above. Applied after the properties on this type.
    /// </summary>
    public Action<OpenXmlConfiguration>? Configure { get; set; }

    /// <summary>Builds the MiniExcel configuration these options describe.</summary>
    internal OpenXmlConfiguration CreateConfiguration()
    {
        var configuration = new OpenXmlConfiguration { Culture = Culture };
        Configure?.Invoke(configuration);
        return configuration;
    }
}

/// <summary>Settings for reading an Excel worksheet.</summary>
public sealed class ExcelSourceOptions : ExcelOptions
{
    /// <summary>Whether the first row holds column names. Defaults to <see langword="true"/>.</summary>
    /// <remarks>
    /// With no header row the columns are addressed by their spreadsheet letters — <c>A</c>, <c>B</c>,
    /// <c>C</c> — which is what a row type's properties are then matched against.
    /// </remarks>
    public bool HasHeaderRow { get; set; } = true;

    /// <summary>The cell the data starts at. Defaults to <c>A1</c>.</summary>
    public string StartCell { get; set; } = "A1";

    /// <summary>
    /// Whether a row that cannot be converted to the row type is skipped rather than failing the run.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// One unconvertible cell costing a million-row load is as unwelcome here as it is in a CSV, so
    /// skipping is the default. Skipped rows are counted on
    /// <see cref="ExcelSource{TRow}.MalformedRows"/> and their cell values are handed to an
    /// <see cref="IDeadLetterSink{TRow}"/> of <see cref="string"/> when one is supplied, so nothing is
    /// lost silently. Set this to <see langword="false"/> when the workbook is meant to be perfect and
    /// anything else should stop the run.
    /// </remarks>
    public bool SkipMalformedRows { get; set; } = true;
}

/// <summary>Settings for writing an Excel worksheet.</summary>
public sealed class ExcelSinkOptions : ExcelOptions
{
    /// <summary>Whether to write a header row of column names. Defaults to <see langword="true"/>.</summary>
    public bool WriteHeader { get; set; } = true;

    /// <summary>
    /// Whether to write through a temporary file and rename it into place once the run succeeds.
    /// Defaults to <see langword="true"/>, and applies only to the file-based sink.
    /// </summary>
    /// <remarks>
    /// This matters more for a workbook than for a CSV. An <c>.xlsx</c> is a zip archive whose central
    /// directory is written last, so a run that fails part-way leaves not a short-but-readable file but
    /// one Excel refuses to open at all. Writing to a temporary file and renaming on success means the
    /// target path either does not exist or holds a workbook that opens.
    /// </remarks>
    public bool WriteAtomically { get; set; } = true;

    /// <summary>
    /// How many rows may sit between the pipeline and the writer before the pipeline is made to wait.
    /// Defaults to 4096.
    /// </summary>
    /// <remarks>
    /// The sink hands rows to MiniExcel through a bounded channel, so this is the back-pressure knob:
    /// large enough that the writer is never starved between batches, small enough that a slow disk
    /// cannot be outrun.
    /// </remarks>
    public int BufferedRows { get; set; } = 4096;
}
