using System.Text.Json;

namespace EtlPipelines.Extensions.Json;

/// <summary>Settings shared by the JSON source and sink.</summary>
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

    /// <summary>
    /// Whether the file is JSON Lines or a single JSON array. Defaults to <see cref="JsonFormat.Lines"/>.
    /// </summary>
    public JsonFormat Format { get; set; } = JsonFormat.Lines;
}

/// <summary>Settings for reading a JSON file.</summary>
public sealed class JsonSourceOptions : JsonOptions
{
    /// <summary>
    /// Whether a line that cannot be parsed is skipped rather than failing the run. Only applies to
    /// <see cref="JsonFormat.Lines"/> - a malformed element always fails the run under
    /// <see cref="JsonFormat.Array"/>, because an array has no per-element recovery boundary to skip
    /// to. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// One unparseable line costing a ten-million-row load is the classic bad-data complaint, so
    /// skipping is the default. Skipped lines are counted on <see cref="JsonSource{TRow}.MalformedRows"/>
    /// and their raw text is handed to an <see cref="IDeadLetterSink{TRow}"/> of <see cref="string"/>
    /// when one is supplied, so nothing is lost silently. Set this to <see langword="false"/> when the
    /// file is meant to be perfect and anything else should stop the run.
    /// </remarks>
    public bool SkipMalformedRows { get; set; } = true;
}

/// <summary>Settings for writing a JSON file.</summary>
public sealed class JsonSinkOptions : JsonOptions
{
    /// <summary>
    /// Whether to write through a temporary file and rename it into place once the run succeeds.
    /// Defaults to <see langword="true"/>, and applies only to the path-based sink.
    /// </summary>
    /// <remarks>
    /// A sink streams batches to disk as they arrive, so a run that fails part-way leaves a truncated
    /// file - which a downstream job may happily consume as if it were complete, or, for
    /// <see cref="JsonFormat.Array"/>, as a file that is not even valid JSON because the closing
    /// <c>]</c> was never written. Writing to a temporary file and renaming on success means the
    /// target path either does not exist or is whole. Turn it off only when something needs to watch
    /// the file grow.
    /// </remarks>
    public bool WriteAtomically { get; set; } = true;
}
