using System.IO.Abstractions;
using EtlPipelines.Extensions.Json;

// ReSharper disable once CheckNamespace
namespace EtlPipelines;

/// <summary>Shorthand for putting a JSON file - JSON Lines or a JSON array - at either end of a dataflow.</summary>
/// <remarks>
/// Files are named as <see cref="IFileInfo"/> rather than as paths, because an
/// <see cref="IFileInfo"/> already carries the filesystem it belongs to. That keeps the connector from
/// ever having to construct one - it has no dependency on a concrete filesystem at all - and it means
/// a test passes <c>mockFileSystem.FileInfo.New("orders.json")</c> and everything downstream follows.
/// <para>
/// Lines and Array are separate method pairs, <c>FromJsonLines</c>/<c>ToJsonLines</c> and
/// <c>FromJsonArray</c>/<c>ToJsonArray</c>, rather than one pair taking a format flag: each pair binds
/// to its own port type (<see cref="JsonLinesSource{TRow}"/>/<see cref="JsonArraySource{TRow}"/> and
/// their sink counterparts) and its own options type, so there is nothing here that names a format one
/// way and behaves according to another.
/// </para>
/// </remarks>
public static class ExtensionsJson
{
    /// <summary>Begins a dataflow reading from a JSON Lines (NDJSON) file.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="file">The file to read.</param>
    /// <param name="options">Format settings, including whether to skip malformed lines.</param>
    /// <remarks>
    /// Registered through the factory overload rather than as an instance, so each run builds its own
    /// source. A shared instance would carry an open file handle and its read position from one run
    /// into the next, and the second run would find itself already at end of file.
    /// </remarks>
    public static IDataflowBuilder<TRow> FromJsonLines<TRow>(
        this IPipelineBuilder builder,
        IFileInfo file,
        JsonLinesSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.From(services => new JsonLinesSource<TRow>(
            file,
            options,
            // Optional: rows that cannot be parsed are recoverable when a dead-letter sink is
            // registered, and merely counted when one is not.
            services.GetService(typeof(IDeadLetterSink<string>)) as IDeadLetterSink<string>));
    }

    /// <summary>Begins a dataflow reading from a file holding a single JSON array.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="file">The file to read.</param>
    /// <param name="options">Format settings.</param>
    public static IDataflowBuilder<TRow> FromJsonArray<TRow>(
        this IPipelineBuilder builder,
        IFileInfo file,
        JsonArraySourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.From(_ => new JsonArraySource<TRow>(file, options));
    }

    /// <summary>Terminates a dataflow by writing to a JSON Lines (NDJSON) file.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="file">The destination file.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    public static IPipelineBuilder ToJsonLines<TRow>(
        this IDataflowBuilder<TRow> builder,
        IFileInfo file,
        JsonSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.To(_ => new JsonLinesSink<TRow>(file, options));
    }

    /// <summary>Terminates a dataflow by writing every row to a single JSON array in one file.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="file">The destination file.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    public static IPipelineBuilder ToJsonArray<TRow>(
        this IDataflowBuilder<TRow> builder,
        IFileInfo file,
        JsonSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.To(_ => new JsonArraySink<TRow>(file, options));
    }
}
