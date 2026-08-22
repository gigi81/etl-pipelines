using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Hosting;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.Tests;

/// <summary>
/// A real directory on a real disk for a sample to write into, and a container wired the way the
/// sample's own command line wires one.
/// </summary>
/// <remarks>
/// Deliberately the real filesystem. These tests exist to prove the samples work as written, and a
/// sample that only ran against an in-memory filesystem would not be evidence of that.
/// </remarks>
public sealed class SampleScratch : IAsyncDisposable
{
    private readonly IFileSystem _fileSystem = new FileSystem();
    private readonly ServiceProvider _provider;

    public SampleScratch(string name, Action<IServiceCollection, IDirectoryInfo> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Directory = _fileSystem.DirectoryInfo.New(
            Path.Combine(Path.GetTempPath(), $"etl-samples-{name}-{Guid.NewGuid():N}"));
        Directory.Create();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(_fileSystem);

        // Under the same key the samples' own command line registers it, so the seeders resolve it
        // here exactly as they do there.
        services.AddKeyedSingleton(SampleWorkspace.Key, Directory);
        services.AddSingleton<PipelineRunner>();
        configure(services, Directory);

        _provider = services.BuildServiceProvider();
    }

    /// <summary>The scratch directory, as a sample sees it.</summary>
    public IDirectoryInfo Directory { get; }

    /// <summary>The container the sample's services were registered into.</summary>
    public IServiceProvider Services => _provider;

    public IFileInfo File(string name) => Directory.File(name);

    public T GetRequiredService<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// Runs the one pipeline the sample registered, and hands back what it did.
    /// </summary>
    /// <remarks>
    /// Resolved as <see cref="IPipeline"/> rather than through the runner, which reports an exit code
    /// and nothing else — these tests want the row counts. That the resolution works at all is worth
    /// something too: it is what lets a host discover an application's pipelines.
    /// </remarks>
    public Task<ErrorOr<PipelineResult>> RunAsync(CancellationToken cancellationToken = default) =>
        _provider.GetServices<IPipeline>().Single().RunAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();

        try
        {
            Directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a test over.
        }
    }
}
