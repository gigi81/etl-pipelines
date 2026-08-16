namespace EtlPipelines.Csv;

/// <summary>Shorthand for putting a CSV file at either end of a dataflow.</summary>
public static class CsvBuilderExtensions
{
    /// <summary>Begins a dataflow reading from a CSV file.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="path">Path to the file to read.</param>
    /// <param name="options">Format settings.</param>
    /// <remarks>
    /// Registered through the factory overload rather than as an instance, so each run builds its own
    /// source. A shared instance would carry an open file handle and its read position from one run
    /// into the next, and the second run would find itself already at end of file.
    /// </remarks>
    public static IDataflowBuilder<TRow> FromCsv<TRow>(
        this IPipelineBuilder builder,
        string path,
        CsvSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return builder.From(services => new CsvSource<TRow>(
            path,
            options,
            // Optional: rows that cannot be parsed are recoverable when a dead-letter sink is
            // registered, and merely counted when one is not.
            services.GetService(typeof(IDeadLetterSink<string>)) as IDeadLetterSink<string>));
    }

    /// <summary>Terminates a dataflow by writing to a CSV file.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="path">Destination path.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    public static IPipelineBuilder ToCsv<TRow>(
        this IDataflowBuilder<TRow> builder,
        string path,
        CsvSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return builder.To(_ => new CsvSink<TRow>(path, options));
    }
}
