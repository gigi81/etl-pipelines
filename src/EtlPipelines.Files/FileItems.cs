using System.IO.Abstractions;

namespace EtlPipelines.Files;

/// <summary>
/// Reads back what a file stage produced, published into <see cref="PipelineContext.Items"/> - whose
/// own documentation names "a downloaded file path" as the case it exists for.
/// </summary>
/// <remarks>
/// The primary way to hand a file to a later stage is still to compose against a known
/// <see cref="IFileInfo"/> up front, the way <c>FromCsv&lt;T&gt;</c> always has - an
/// <see cref="IFileInfo"/> that does not exist yet is a legitimate thing to name, as long as the
/// stage that reads it refreshes first. This class exists for what composition cannot know in
/// advance: which files a pattern actually matched, or what a download's URL list produced.
/// </remarks>
public static class FileItems
{
    private const string Prefix = "etl.files.produced";

    /// <summary>The files the most recently run file stage produced. Empty when none has run yet.</summary>
    public static IReadOnlyList<IFileInfo> ProducedFiles(this PipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(Prefix, out var value) && value is IReadOnlyList<IFileInfo> files
            ? files
            : [];
    }

    /// <summary>
    /// The files published under <paramref name="key"/> - a stage's <c>PublishAs</c> option, or its
    /// name when that was left unset. Empty when no stage has published under that key.
    /// </summary>
    public static IReadOnlyList<IFileInfo> ProducedFiles(this PipelineContext context, string key)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return context.Items.TryGetValue($"{Prefix}.{key}", out var value) && value is IReadOnlyList<IFileInfo> files
            ? files
            : [];
    }

    /// <summary>
    /// Publishes what a file stage produced, under both the shared "most recent" key and its own
    /// <paramref name="key"/>. Called before a stage returns, on the failure path too, so a
    /// half-finished drain still tells a later stage - or an operator reading the failed run - what
    /// landed.
    /// </summary>
    /// <remarks>
    /// Public, rather than internal to this package, because the stages in the Http and Sftp
    /// satellite packages publish through this too.
    /// </remarks>
    public static void PublishProducedFiles(this PipelineContext context, string key, IReadOnlyList<IFileInfo> files)
    {
        context.Items[Prefix] = files;
        context.Items[$"{Prefix}.{key}"] = files;
    }
}
