using System.IO.Abstractions;

// ReSharper disable once CheckNamespace
namespace EtlPipelines;

/// <summary>Copying, moving, compressing and extracting files as pipeline stages.</summary>
/// <remarks>
/// Files are named as <see cref="IFileInfo"/>/<see cref="IDirectoryInfo"/> rather than as paths, for
/// the same reason every other connector in this library does it: the file already carries the
/// filesystem it belongs to, so a stage never has to construct one, and a test passes
/// <c>mockFileSystem.FileInfo.New(...)</c> and everything downstream follows.
/// </remarks>
public static class ExtensionsFiles
{
    /// <summary>Copies one file to another.</summary>
    public static IPipelineBuilder CopyFile(
        this IPipelineBuilder builder,
        IFileInfo source,
        IFileInfo target,
        Action<FileCopyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var options = new FileCopyOptions();
        configure?.Invoke(options);

        var name = options.Name ?? $"copy {source.Name} -> {target.Name}";

        return builder.AddStage(new FileTransferStage(
            FileTransferMode.Copy,
            new SingleFileSelection(source),
            _ => target,
            SettingsFrom(options, writeAtomically: true),
            name,
            options.PublishAs));
    }

    /// <summary>Copies every file matching <paramref name="pattern"/> in <paramref name="source"/> into <paramref name="target"/>.</summary>
    public static IPipelineBuilder CopyFiles(
        this IPipelineBuilder builder,
        IDirectoryInfo source,
        string pattern,
        IDirectoryInfo target,
        Action<FileCopyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(target);

        var options = new FileCopyOptions();
        configure?.Invoke(options);

        return builder.CopyFiles(new PatternFileSelection(source, pattern, options), target, options);
    }

    /// <summary>Copies every file in <paramref name="selection"/> into <paramref name="target"/>.</summary>
    public static IPipelineBuilder CopyFiles(
        this IPipelineBuilder builder,
        IFileSelection selection,
        IDirectoryInfo target,
        Action<FileCopyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new FileCopyOptions();
        configure?.Invoke(options);

        return builder.CopyFiles(selection, target, options);
    }

    private static IPipelineBuilder CopyFiles(
        this IPipelineBuilder builder, IFileSelection selection, IDirectoryInfo target, FileCopyOptions options)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(target);

        var name = options.Name ?? $"copy {selection.Name} -> {target.Name}";

        return builder.AddStage(new FileTransferStage(
            FileTransferMode.Copy,
            selection,
            source => target.File(source.Name),
            SettingsFrom(options, writeAtomically: true),
            name,
            options.PublishAs));
    }

    /// <summary>Moves one file to another.</summary>
    public static IPipelineBuilder MoveFile(
        this IPipelineBuilder builder,
        IFileInfo source,
        IFileInfo target,
        Action<FileMoveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var options = new FileMoveOptions();
        configure?.Invoke(options);

        var name = options.Name ?? $"move {source.Name} -> {target.Name}";

        return builder.AddStage(new FileTransferStage(
            FileTransferMode.Move,
            new SingleFileSelection(source),
            _ => target,
            SettingsFrom(options, options.WriteAtomically),
            name,
            options.PublishAs));
    }

    /// <summary>Moves every file matching <paramref name="pattern"/> in <paramref name="source"/> into <paramref name="target"/>.</summary>
    public static IPipelineBuilder MoveFiles(
        this IPipelineBuilder builder,
        IDirectoryInfo source,
        string pattern,
        IDirectoryInfo target,
        Action<FileMoveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(target);

        var options = new FileMoveOptions();
        configure?.Invoke(options);

        return builder.MoveFiles(new PatternFileSelection(source, pattern, options), target, options);
    }

    /// <summary>Moves every file in <paramref name="selection"/> into <paramref name="target"/>.</summary>
    public static IPipelineBuilder MoveFiles(
        this IPipelineBuilder builder,
        IFileSelection selection,
        IDirectoryInfo target,
        Action<FileMoveOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new FileMoveOptions();
        configure?.Invoke(options);

        return builder.MoveFiles(selection, target, options);
    }

    private static IPipelineBuilder MoveFiles(
        this IPipelineBuilder builder, IFileSelection selection, IDirectoryInfo target, FileMoveOptions options)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(target);

        var name = options.Name ?? $"move {selection.Name} -> {target.Name}";

        return builder.AddStage(new FileTransferStage(
            FileTransferMode.Move,
            selection,
            source => target.File(source.Name),
            SettingsFrom(options, options.WriteAtomically),
            name,
            options.PublishAs));
    }

    /// <summary>Compresses one file into an archive.</summary>
    public static IPipelineBuilder CompressFile(
        this IPipelineBuilder builder,
        IFileInfo source,
        IFileInfo archive,
        Action<CompressOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(archive);

        var options = new CompressOptions();
        configure?.Invoke(options);

        return builder.AddStage(new CompressStage(new SingleFileSelection(source), null, archive, options));
    }

    /// <summary>Compresses every file matching <paramref name="pattern"/> in <paramref name="source"/> into an archive.</summary>
    public static IPipelineBuilder CompressFiles(
        this IPipelineBuilder builder,
        IDirectoryInfo source,
        string pattern,
        IFileInfo archive,
        Action<CompressOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(archive);

        var options = new CompressOptions();
        configure?.Invoke(options);

        return builder.AddStage(new CompressStage(new PatternFileSelection(source, pattern, options), source, archive, options));
    }

    /// <summary>Compresses every file in <paramref name="selection"/> into an archive.</summary>
    public static IPipelineBuilder CompressFiles(
        this IPipelineBuilder builder,
        IFileSelection selection,
        IFileInfo archive,
        Action<CompressOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(archive);

        var options = new CompressOptions();
        configure?.Invoke(options);

        return builder.AddStage(new CompressStage(selection, null, archive, options));
    }

    /// <summary>Extracts an archive into a directory.</summary>
    public static IPipelineBuilder ExtractArchive(
        this IPipelineBuilder builder,
        IFileInfo archive,
        IDirectoryInfo target,
        Action<ExtractOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(target);

        var options = new ExtractOptions();
        configure?.Invoke(options);

        return builder.AddStage(new ExtractStage(archive, target, options));
    }

    private static FileTransferSettings SettingsFrom(FileSelectionOptions options, bool writeAtomically) =>
        new(options.Overwrite, options.CreateTargetDirectory, writeAtomically, options.OnFileError, options.MaxConcurrency);
}
