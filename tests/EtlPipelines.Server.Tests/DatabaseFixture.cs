using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;

namespace EtlPipelines.Server.Tests;

/// <summary>The connection string of a database a test can run against.</summary>
public interface IDatabaseFixture
{
    string ConnectionString { get; }
}

/// <summary>
/// Starts one Testcontainers database and keeps it up for as long as the fixture is shared.
/// </summary>
/// <remarks>
/// The same shape as <c>tests/EtlPipelines.Server.Database.Tests/DatabaseFixture.cs</c> -
/// duplicated here rather than referenced, matching how this repo's test projects generally keep
/// their own copies of small fixtures. Declared with
/// <c>[ClassDataSource&lt;T&gt;(Shared = SharedType.PerAssembly)]</c> the container is started
/// once for the whole test project rather than once per test class.
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

/// <summary>
/// A genuinely empty Postgres container - nothing has run <c>db/server/_Init.sql</c> against it
/// yet, which is exactly the state <see cref="ManagementServiceDockerTests"/> needs to prove
/// <c>ManagementService</c> works end to end against a real database, not just SQLite.
/// </summary>
public sealed class PostgreSqlFixture : DatabaseFixture<PostgreSqlContainer>
{
    // Same image tag every other Postgres fixture in this repo pins to.
    private const string Image = "postgres:15.1";

    // Named to match db/dbsettings.json's own "server" database, so the container is already the
    // right database, empty, the moment it starts.
    protected override PostgreSqlContainer CreateContainer() =>
        new PostgreSqlBuilder(Image)
            .WithDatabase("etlpipelines_server")
            .Build();
}
