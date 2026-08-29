using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Files.Selection;

/// <summary>Which local files a stage works on.</summary>
/// <remarks>
/// Resolved when the stage runs, not when the pipeline is composed - an earlier stage in the same run
/// may be exactly what produced the files this one selects.
/// </remarks>
public interface IFileSelection
{
    /// <summary>What the selection is called, in the run's report and in errors.</summary>
    string Name { get; }

    /// <summary>Resolves the files, in a deterministic order.</summary>
    ValueTask<ErrorOr<IReadOnlyList<IFileInfo>>> ResolveAsync(CancellationToken cancellationToken);
}
