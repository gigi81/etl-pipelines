using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using EtlPipelines.Abstractions.Execution;

namespace EtlPipelines.Files.Tests;

/// <summary>Shared helpers for building pipelines against an in-memory filesystem.</summary>
internal static class Fixtures
{
    /// <summary>Runs a single stage in a standalone pipeline, the same way samples build one.</summary>
    public static Task<ErrorOr<PipelineResult>> RunAsync(this IPipelineStage stage) =>
        EtlPipeline.CreateBuilder("test").AddStage(stage).Build().RunAsync(CancellationToken.None);

    /// <summary>A MockFileSystem rooted at "/work".</summary>
    public static MockFileSystem NewFileSystem() => new(new MockFileSystemOptions
    {
        CurrentDirectory = "/work",
    });
}

/// <summary>
/// A selection returning exactly the files it is given, in that order - including one that does not
/// exist. Used to force a deterministic mid-batch failure: MockFileSystem does not reproduce every
/// real-disk failure mode (moving onto an existing directory, for one), but it reliably throws
/// <see cref="FileNotFoundException"/> for a file that was never added.
/// </summary>
internal sealed class FixedFileSelection(string name, params IFileInfo[] files) : IFileSelection
{
    public string Name => name;

    public ValueTask<ErrorOr<IReadOnlyList<IFileInfo>>> ResolveAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ErrorOr<IReadOnlyList<IFileInfo>>>(files);
}
