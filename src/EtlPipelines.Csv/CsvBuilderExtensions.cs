using System.IO.Abstractions;

namespace EtlPipelines.Csv;

/// <summary>Shorthand for putting a CSV file at either end of a dataflow.</summary>
/// <remarks>
/// Files are named as <see cref="IFileInfo"/> rather than as paths, because an
/// <see cref="IFileInfo"/> already carries the filesystem it belongs to. That keeps the connector from
/// ever having to construct one — it has no dependency on a concrete filesystem at all — and it means
/// a test passes <c>mockFileSystem.FileInfo.New("orders.csv")</c> and everything downstream follows.
/// </remarks>
public static class CsvBuilderExtensions
{
    /// <summary>Begins a dataflow reading from a CSV file.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="file">The file to read.</param>
    /// <param name="options">Format settings.</param>
    /// <remarks>
    /// Registered through the factory overload rather than as an instance, so each run builds its own
    /// source. A shared instance would carry an open file handle and its read position from one run
    /// into the next, and the second run would find itself already at end of file.
    /// </remarks>
    public static IDataflowBuilder<TRow> FromCsv<TRow>(
        this IPipelineBuilder builder,
        IFileInfo file,
        CsvSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.From(services => new CsvSource<TRow>(
            file,
            options,
            // Optional: rows that cannot be parsed are recoverable when a dead-letter sink is
            // registered, and merely counted when one is not.
            services.GetService(typeof(IDeadLetterSink<string>)) as IDeadLetterSink<string>));
    }

    /// <summary>Terminates a dataflow by writing to a CSV file.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="file">The destination file.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    public static IPipelineBuilder ToCsv<TRow>(
        this IDataflowBuilder<TRow> builder,
        IFileInfo file,
        CsvSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(file);

        return builder.To(_ => new CsvSink<TRow>(file, options));
    }
}
