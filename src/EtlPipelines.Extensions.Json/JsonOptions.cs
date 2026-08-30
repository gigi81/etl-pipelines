using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>Settings shared by every JSON source and sink, whatever the on-disk shape.</summary>
public abstract class JsonOptions
{
    /// <summary>
    /// Controls property naming, casing, converters and indentation.
    /// </summary>
    /// <remarks>
    /// Defaults to a fresh instance seeded from <see cref="JsonSerializerDefaults.Web"/> - camelCase
    /// properties, case-insensitive matching on read - because that is what a JSON file produced
    /// outside .NET almost always looks like, and a file this library wrote itself round-trips
    /// regardless of casing since matching on read is case-insensitive either way.
    /// <para>
    /// Built with <c>new JsonSerializerOptions(JsonSerializerDefaults.Web)</c> rather than the
    /// <see cref="JsonSerializerOptions.Web"/> singleton on purpose: that singleton comes back
    /// already <see cref="JsonSerializerOptions.IsReadOnly"/>, so a caller who tries to add a
    /// converter or flip <see cref="JsonSerializerOptions.WriteIndented"/> on it gets an
    /// <see cref="InvalidOperationException"/> instead of the change they asked for. This instance
    /// is unlocked until first used, so it is safe to mutate in an object initializer.
    /// </para>
    /// </remarks>
    public JsonSerializerOptions SerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
}

/// <summary>Settings for reading JSON Lines (NDJSON) - one JSON value per line.</summary>
public sealed class JsonLinesSourceOptions : JsonOptions
{
    /// <summary>
    /// Whether a line that cannot be parsed is skipped rather than failing the run. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// One unparseable line costing a ten-million-row load is the classic bad-data complaint, so
    /// skipping is the default. Skipped lines are counted on <see cref="JsonLinesSource{TRow}.MalformedRows"/>
    /// and their raw text is handed to an <see cref="IDeadLetterSink{TRow}"/> of <see cref="string"/>
    /// when one is supplied, so nothing is lost silently. Set this to <see langword="false"/> when the
    /// file is meant to be perfect and anything else should stop the run.
    /// </remarks>
    public bool SkipMalformedRows { get; set; } = true;
}

/// <summary>Settings for reading a single JSON array holding every row.</summary>
/// <remarks>
/// A separate type from <see cref="JsonLinesSourceOptions"/> - rather than the two sharing one type
/// with a field that means nothing for this format - because <see cref="JsonArraySource{TRow}"/> has
/// no equivalent of <see cref="JsonLinesSourceOptions.SkipMalformedRows"/>: unlike a line, a
/// malformed element inside an array has no recovery boundary to skip to, so there would be nothing
/// here for that setting to do.
/// </remarks>
public sealed class JsonArraySourceOptions : JsonOptions
{
    /// <summary>
    /// The property names to walk from the root object down to the array to read, outermost first.
    /// Empty - the default - means the array <i>is</i> the document's root value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For <c>{"result": {"rows": [ ... ]}}</c>, <c>Path</c> is <c>["result", "rows"]</c>. Every
    /// property along the way must name a JSON object, and the last one must name a JSON array;
    /// anything else - the property missing, or naming something else - fails the run.
    /// </para>
    /// <para>
    /// Rows still arrive one at a time without buffering the array in memory, the same as the root
    /// case. What is not fully streamed is the walk down to the array itself: <see cref="JsonArraySource{TRow}"/>
    /// re-attempts that walk from the buffered start of the document each time more data arrives,
    /// rather than resuming a partial walk. That is fine for what <c>Path</c> is for - navigating
    /// past a small wrapper object such as an envelope or a pagination header - but it means a very
    /// large sibling value positioned <i>before</i> the array in the document is buffered while
    /// skipped over, rather than streamed past.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Path { get; set; } = [];
}

/// <summary>Settings for writing a JSON file, either JSON Lines or a single array.</summary>
/// <remarks>
/// One type shared by <see cref="JsonLinesSink{TRow}"/> and <see cref="JsonArraySink{TRow}"/>, unlike
/// the source side: both write atomically the same way, and neither has a setting the other lacks.
/// </remarks>
public sealed class JsonSinkOptions : JsonOptions
{
    /// <summary>
    /// Whether to write through a temporary file and rename it into place once the run succeeds.
    /// Defaults to <see langword="true"/>, and applies only to the path-based sink.
    /// </summary>
    /// <remarks>
    /// A sink streams batches to disk as they arrive, so a run that fails part-way leaves a truncated
    /// file - which a downstream job may happily consume as if it were complete, or, from
    /// <see cref="JsonArraySink{TRow}"/>, as a file that is not even valid JSON because the closing
    /// <c>]</c> was never written. Writing to a temporary file and renaming on success means the
    /// target path either does not exist or is whole. Turn it off only when something needs to watch
    /// the file grow.
    /// </remarks>
    public bool WriteAtomically { get; set; } = true;
}
