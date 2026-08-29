using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace EtlPipelines.Extensions.Csv;

/// <summary>Settings shared by the CSV source and sink.</summary>
public abstract class CsvOptions
{
    /// <summary>
    /// The culture used to parse and format field values. Defaults to
    /// <see cref="CultureInfo.InvariantCulture"/>.
    /// </summary>
    /// <remarks>
    /// This default is a correctness decision, not a preference. Under a machine-local culture a
    /// decimal written as <c>1.5</c> reads back as <c>1,5</c> on a host configured for a
    /// comma separator — and with a comma delimiter it splits into two fields instead. A data file
    /// must mean the same thing wherever it is processed, so the culture belongs to the file format,
    /// not to the machine. Override it only when a file genuinely carries locale-formatted values.
    /// </remarks>
    public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>Whether the file has a header row. Defaults to <see langword="true"/>.</summary>
    public bool HasHeaderRecord { get; set; } = true;

    /// <summary>Field delimiter. Defaults to a comma.</summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>Text encoding. Defaults to UTF-8 without a byte order mark.</summary>
    public Encoding Encoding { get; set; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Escape hatch for anything not surfaced above — trimming, comments, quoting, bad-data
    /// callbacks. Applied after the properties on this type.
    /// </summary>
    public Action<CsvConfiguration>? Configure { get; set; }

    /// <summary>
    /// Hook for registering class maps and type converters, which live on the context rather than
    /// the configuration.
    /// </summary>
    public Action<CsvContext>? ConfigureContext { get; set; }

    /// <summary>Builds the CsvHelper configuration these options describe.</summary>
    internal CsvConfiguration CreateConfiguration()
    {
        var configuration = new CsvConfiguration(Culture)
        {
            HasHeaderRecord = HasHeaderRecord,
            Delimiter = Delimiter,
        };

        Configure?.Invoke(configuration);
        return configuration;
    }
}

/// <summary>Settings for reading a CSV file.</summary>
public sealed class CsvSourceOptions : CsvOptions
{
    /// <summary>
    /// Whether a row that cannot be parsed is skipped rather than failing the run. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// One unparseable row costing a ten-million-row load is the classic CSV complaint, so skipping
    /// is the default. Skipped rows are counted on <see cref="CsvSource{TRow}.MalformedRows"/> and
    /// their raw text is handed to an <see cref="IDeadLetterSink{TRow}"/> of <see cref="string"/> when
    /// one is supplied, so nothing is lost silently. Set this to <see langword="false"/> when the file
    /// is meant to be perfect and anything else should stop the run.
    /// </remarks>
    public bool SkipMalformedRows { get; set; } = true;
}

/// <summary>Settings for writing a CSV file.</summary>
public sealed class CsvSinkOptions : CsvOptions
{
    /// <summary>
    /// Whether to write through a temporary file and rename it into place once the run succeeds.
    /// Defaults to <see langword="true"/>, and applies only to the path-based sink.
    /// </summary>
    /// <remarks>
    /// A sink streams batches to disk as they arrive, so a run that fails part-way leaves a truncated
    /// file — which a downstream job may happily consume as if it were complete. Writing to a
    /// temporary file and renaming on success means the target path either does not exist or is
    /// whole. Turn it off only when something needs to watch the file grow.
    /// </remarks>
    public bool WriteAtomically { get; set; } = true;
}
