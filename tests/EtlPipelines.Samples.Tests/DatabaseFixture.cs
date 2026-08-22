using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace EtlPipelines.Samples.Tests;

/// <summary>The connection string of a database a test can run against.</summary>
public interface IDatabaseFixture
{
    string ConnectionString { get; }
}

/// <summary>
/// Starts one Testcontainers database and keeps it up for as long as the fixture is shared.
/// </summary>
/// <remarks>
/// Declared with <c>[ClassDataSource&lt;T&gt;(Shared = SharedType.PerAssembly)]</c> the container is
/// started once for the whole test project rather than once per test class. Starting a database is by
/// far the most expensive thing these tests do — an Oracle one costs the best part of a minute — so
/// paying for it once matters.
/// </remarks>
public abstract class DatabaseFixture<TContainer> : IDatabaseFixture, IAsyncInitializer, IAsyncDisposable
    where TContainer : DockerContainer, IDatabaseContainer
{
    private readonly Lazy<TContainer> _container;

    protected DatabaseFixture() => _container = new Lazy<TContainer>(CreateContainer);

    protected abstract TContainer CreateContainer();

    public TContainer Container => _container.Value;

    public virtual string ConnectionString => Container.GetConnectionString();

    public virtual Task InitializeAsync() => Container.StartAsync();

    public virtual ValueTask DisposeAsync() => Container.DisposeAsync();
}
