using System.IO.Abstractions;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Core;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Extensions.Cli.Tests;

/// <summary>
/// A container plus a real scratch directory, wired the way an application would wire them.
/// </summary>
/// <remarks>
/// Real, not mocked: a script has to actually exist on disk for <c>pwsh</c>/<c>powershell.exe</c> to
/// read it, so unlike most of this library's test hosts, this one is never backed by a
/// <c>MockFileSystem</c> - the same unmocked-integration posture the Sqlite tests take, run for real
/// on every CI leg rather than gated behind a container.
/// </remarks>
public sealed class CliTestHost : IDisposable
{
    private readonly ServiceCollection _services = [];
    private ServiceProvider? _provider;

    public CliTestHost()
    {
        FileSystem = new FileSystem();
        Root = FileSystem.Directory.CreateTempSubdirectory("etl-cli-");
    }

    /// <summary>The real filesystem every script in this test belongs to.</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>A real scratch directory the test's files live in, deleted when the host is disposed.</summary>
    public IDirectoryInfo Root { get; }

    /// <summary>A file inside <see cref="Root"/>, which need not exist yet.</summary>
    public IFileInfo File(string name) => Root.File(name);

    /// <summary>Writes <paramref name="contents"/> to a real file inside <see cref="Root"/>.</summary>
    public IFileInfo Script(string name, string contents)
    {
        var file = File(name);
        FileSystem.File.WriteAllText(file.FullName, contents);
        return file;
    }

    /// <summary>Registers additional services.</summary>
    public CliTestHost Configure(Action<IServiceCollection> configure)
    {
        ThrowIfBuilt();
        configure(_services);
        return this;
    }

    /// <summary>Registers a named pipeline, exactly as an application's startup would.</summary>
    public CliTestHost AddEtlPipeline(string name, Action<IPipelineBuilder> build)
    {
        ThrowIfBuilt();
        _services.AddEtlPipeline(name, build);
        return this;
    }

    /// <summary>The container, built on first use.</summary>
    public IServiceProvider Services => _provider ??= _services.BuildServiceProvider();

    /// <summary>Resolves a pipeline by name and runs it.</summary>
    public Task<ErrorOr<PipelineResult>> RunAsync(string name, CancellationToken cancellationToken = default) =>
        Services.GetRequiredEtlPipeline(name).RunAsync(cancellationToken);

    /// <summary>Deletes the scratch directory this test wrote into.</summary>
    public void Dispose()
    {
        _provider?.Dispose();

        Root.Refresh();
        if (Root.Exists)
        {
            Root.Delete(recursive: true);
        }
    }

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
