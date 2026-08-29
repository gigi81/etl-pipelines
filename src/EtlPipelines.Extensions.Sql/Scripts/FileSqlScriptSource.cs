using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Sql.Scripts;

/// <summary>Reads a script from a file.</summary>
public sealed class FileSqlScriptSource : ISqlScriptSource
{
    private readonly IFileInfo _file;

    /// <summary>Reads from <paramref name="file"/>.</summary>
    public FileSqlScriptSource(IFileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);

        _file = file;
    }

    /// <inheritdoc />
    public string Name => _file.Name;

    /// <inheritdoc />
    public ValueTask<ErrorOr<TextReader>> OpenAsync(CancellationToken cancellationToken)
    {
        // Refreshed rather than trusted: IFileInfo caches what it found when it was created, and a
        // script perfectly well may not exist until an earlier stage of this same run has fetched it.
        _file.Refresh();

        if (!_file.Exists)
        {
            return ValueTask.FromResult<ErrorOr<TextReader>>(
                Error.Failure("sql.script.missing", $"The script '{_file.FullName}' does not exist."));
        }

        return ValueTask.FromResult<ErrorOr<TextReader>>(new StreamReader(_file.OpenRead()));
    }
}
