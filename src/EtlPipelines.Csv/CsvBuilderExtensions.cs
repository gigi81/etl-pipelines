using System.IO.Abstractions;

namespace EtlPipelines.Csv;

/// <summary>Shorthand for putting a CSV file at either end of a dataflow.</summary>
/// <remarks>
/// Both helpers take the <see cref="IFileSystem"/> from the container when one is registered, so a
/// test can register a <c>MockFileSystem</c> once and have every CSV port in the pipeline pick it up.
/// With nothing registered they fall back to the real filesystem.
/// </remarks>
public static class CsvBuilderExtensions
{
    /// <summary>
    /// Picks the filesystem: the one passed in, else one registered in the container, else the real
    /// one. The explicit argument matters for pipelines built without a container.
    /// </summary>
    private static IFileSystem FileSystemFor(IServiceProvider services, IFileSystem? supplied) =>
        supplied ?? services.GetService(typeof(IFileSystem)) as IFileSystem ?? new FileSystem();

    /// <summary>Begins a dataflow reading from a CSV file.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="path">Path to the file to read.</param>
    /// <param name="options">Format settings.</param>
    /// <param name="fileSystem">
    /// Filesystem to read through. Omit to take one from the container, or the real one.
    /// </param>
    /// <remarks>
    /// Registered through the factory overload rather than as an instance, so each run builds its own
    /// source. A shared instance would carry an open file handle and its read position from one run
    /// into the next, and the second run would find itself already at end of file.
    /// </remarks>
    public static IDataflowBuilder<TRow> FromCsv<TRow>(
        this IPipelineBuilder builder,
        string path,
        CsvSourceOptions? options = null,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return builder.From(services => new CsvSource<TRow>(
            path,
            options,
            // Optional: rows that cannot be parsed are recoverable when a dead-letter sink is
            // registered, and merely counted when one is not.
            services.GetService(typeof(IDeadLetterSink<string>)) as IDeadLetterSink<string>,
            FileSystemFor(services, fileSystem)));
    }

    /// <summary>Terminates a dataflow by writing to a CSV file.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="path">Destination path.</param>
    /// <param name="options">Format settings, including whether to write atomically.</param>
    /// <param name="fileSystem">
    /// Filesystem to write through. Omit to take one from the container, or the real one.
    /// </param>
    public static IPipelineBuilder ToCsv<TRow>(
        this IDataflowBuilder<TRow> builder,
        string path,
        CsvSinkOptions? options = null,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return builder.To(services => new CsvSink<TRow>(path, options, FileSystemFor(services, fileSystem)));
    }
}
