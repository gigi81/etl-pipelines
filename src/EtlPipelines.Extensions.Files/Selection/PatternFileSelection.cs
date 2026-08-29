using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Files.Selection;

/// <summary>Selects every file in a directory matching a pattern of <c>*</c> and <c>?</c>.</summary>
public sealed class PatternFileSelection : IFileSelection
{
    private readonly IDirectoryInfo _directory;
    private readonly string _pattern;
    private readonly FileSelectionOptions _options;

    /// <summary>Selects files matching <paramref name="pattern"/> in <paramref name="directory"/>.</summary>
    public PatternFileSelection(IDirectoryInfo directory, string pattern, FileSelectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(options);

        _directory = directory;
        _pattern = pattern;
        _options = options;
    }

    /// <inheritdoc />
    public string Name => $"{_directory.FullName}/{_pattern}";

    /// <inheritdoc />
    public ValueTask<ErrorOr<IReadOnlyList<IFileInfo>>> ResolveAsync(CancellationToken cancellationToken)
    {
        // Refreshed rather than trusted, for the same reason a single selection is: an earlier stage
        // of this same run may be exactly what populated this directory.
        _directory.Refresh();

        if (!_directory.Exists)
        {
            return ValueTask.FromResult<ErrorOr<IReadOnlyList<IFileInfo>>>(
                Error.Failure("files.selection.missing", $"The directory '{_directory.FullName}' does not exist."));
        }

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = _options.Recursive,
            // Simple, not the platform default: on Windows the default is Win32, whose legacy DOS-8.3
            // semantics make "*.htm" also match "report.html". A pattern must mean the same thing on
            // every host that runs the pipeline, not just the one it was written on.
            MatchType = MatchType.Simple,
            MatchCasing = _options.MatchCasing,
            IgnoreInaccessible = _options.IgnoreInaccessible,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        IFileInfo[] matches = [.. _directory.EnumerateFiles(_pattern, enumeration)];

        matches = Sort(matches, _options.Order, _options.Descending);

        if (matches.Length < _options.MinimumFiles)
        {
            return ValueTask.FromResult<ErrorOr<IReadOnlyList<IFileInfo>>>(
                Error.Failure(
                    "files.selection.empty",
                    $"'{Name}' matched {matches.Length} file(s), fewer than the {_options.MinimumFiles} required."));
        }

        return ValueTask.FromResult<ErrorOr<IReadOnlyList<IFileInfo>>>(matches);
    }

    private static IFileInfo[] Sort(IFileInfo[] files, FileOrder order, bool descending)
    {
        // Ordinal throughout, independent of the pattern's own MatchCasing: this sort is about making
        // a rerun deterministic, not about how the pattern matched, and a fixed comparer means that
        // stays true on every host.
        IOrderedEnumerable<IFileInfo> ordered = order switch
        {
            FileOrder.LastWriteTime => files.OrderBy(f => f.LastWriteTimeUtc),
            FileOrder.Length => files.OrderBy(f => f.Length),
            _ => files.OrderBy(f => f.FullName, StringComparer.Ordinal),
        };

        return (descending ? ordered.Reverse() : ordered).ToArray();
    }
}
