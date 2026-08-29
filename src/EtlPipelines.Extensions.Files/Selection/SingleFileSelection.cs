using System.IO.Abstractions;

namespace EtlPipelines.Files.Selection;

/// <summary>Selects exactly one named file.</summary>
public sealed class SingleFileSelection : IFileSelection
{
    private readonly IFileInfo _file;

    /// <summary>Selects <paramref name="file"/>.</summary>
    public SingleFileSelection(IFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);

        _file = file;
    }

    /// <inheritdoc />
    public string Name => _file.Name;

    /// <inheritdoc />
    public ValueTask<ErrorOr<IReadOnlyList<IFileInfo>>> ResolveAsync(CancellationToken cancellationToken)
    {
        // Refreshed rather than trusted: IFileInfo caches what it found when it was created, and a
        // file perfectly well may not exist until an earlier stage of this same run produced it.
        _file.Refresh();

        if (!_file.Exists)
        {
            return ValueTask.FromResult<ErrorOr<IReadOnlyList<IFileInfo>>>(
                Error.Failure("files.selection.missing", $"The file '{_file.FullName}' does not exist."));
        }

        return ValueTask.FromResult<ErrorOr<IReadOnlyList<IFileInfo>>>(
            new IFileInfo[] { _file });
    }
}
