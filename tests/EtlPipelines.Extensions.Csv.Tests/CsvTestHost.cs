using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Csv.Tests;

/// <summary>
/// A container plus a filesystem, wired the way an application would wire them.
/// </summary>
/// <remarks>
/// <para>
/// The tests compose pipelines through <c>AddEtlPipeline</c> and resolve them through
/// <c>GetRequiredEtlPipeline</c>, rather than through the standalone <c>EtlPipeline.CreateBuilder</c>.
/// That is the path a real application takes, and it is the only one that exercises the parts that
/// matter: components registered scoped and keyed to their pipeline, resolved from the scope each run
/// creates, and optional collaborators — a dead-letter sink, for one — picked up from the container.
/// </para>
/// <para>
/// The filesystem is registered as a service too. The CSV ports take an <see cref="IFileInfo"/>, which
/// already knows its own filesystem, so this registration is what lets a test build those file handles
/// from the same in-memory filesystem the rest of the container sees.
/// </para>
/// </remarks>
public sealed class CsvTestHost
{
    private readonly ServiceCollection _services = [];
    private ServiceProvider? _provider;

    /// <summary>Creates a host over an in-memory filesystem with an empty working directory.</summary>
    public CsvTestHost()
        : this(new MockFileSystem())
    {
    }

    /// <summary>Creates a host over a given filesystem — the real one, for the tests that need it.</summary>
    public CsvTestHost(IFileSystem fileSystem)
    {
        FileSystem = fileSystem;
        Root = fileSystem.Directory.CreateTempSubdirectory("etl-csv-");

        _services.AddSingleton(fileSystem);
    }

    /// <summary>The filesystem every file in this test belongs to.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>A scratch directory the test's files live in.</summary>
    public IDirectoryInfo Root { get; }

    /// <summary>A file inside <see cref="Root"/>, which need not exist yet.</summary>
    public IFileInfo File(string name) => Root.File(name);

    /// <summary>Every temporary file a sink has left behind, anywhere under the root.</summary>
    public IFileInfo[] TempFiles() => [.. Root.EnumerateFiles("*.tmp", SearchOption.AllDirectories)];

    /// <summary>Registers additional services — a dead-letter sink, a port, a shared dependency.</summary>
    public CsvTestHost Configure(Action<IServiceCollection> configure)
    {
        ThrowIfBuilt();
        configure(_services);
        return this;
    }

    /// <summary>Registers a named pipeline, exactly as an application's startup would.</summary>
    public CsvTestHost AddEtlPipeline(string name, Action<IPipelineBuilder> build)
    {
        ThrowIfBuilt();
        _services.AddEtlPipeline(name, build);
        return this;
    }

    /// <summary>The service descriptors, for asserting on how components were registered.</summary>
    public IServiceCollection Registrations => _services;

    /// <summary>The container, built on first use.</summary>
    public IServiceProvider Services => _provider ??= _services.BuildServiceProvider();

    /// <summary>Resolves a pipeline by name and runs it.</summary>
    public Task<ErrorOr<PipelineResult>> RunAsync(string name, CancellationToken cancellationToken = default) =>
        Services.GetRequiredEtlPipeline(name).RunAsync(cancellationToken);

    private void ThrowIfBuilt()
    {
        if (_provider is not null)
        {
            throw new InvalidOperationException(
                "The container has already been built. Register everything before the first run, " +
                "because AddEtlPipeline registers a pipeline's components as it composes them.");
        }
    }
}
