namespace EtlPipelines.Extensions.Json;

/// <summary>The on-disk shape of a JSON data file.</summary>
public enum JsonFormat
{
    /// <summary>
    /// One JSON value per line - JSON Lines, also called NDJSON. The default: naturally streamable
    /// in both directions, and, like <c>EtlPipelines.Extensions.Csv.CsvSourceOptions</c>, lets a
    /// single malformed record be skipped and dead-lettered without losing the rest of the file,
    /// because a line is a recovery boundary a parser can resynchronise on.
    /// </summary>
    Lines,

    /// <summary>
    /// A single JSON array holding every row - the shape most tools produce when asked to "export as
    /// JSON", and the shape most non-.NET readers expect. Still read and written a row at a time
    /// rather than buffered whole in memory, but a malformed element has no recovery boundary of its
    /// own: unlike a line, there is no way to skip past just the broken element and keep parsing the
    /// array, so a malformed row fails the whole read.
    /// </summary>
    Array,
}
