using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;

namespace EtlPipelines.Server.Database.Tests;

/// <summary>The connection string of a database a test can run against.</summary>
public interface IDatabaseFixture
{
    string ConnectionString { get; }
}

/// <summary>
/// Starts one Testcontainers database and keeps it up for as long as the fixture is shared.
/// </summary>
/// <remarks>
/// The same shape as <c>tests/EtlPipelines.Extensions.Sql.Databases.Tests/DatabaseFixture.cs</c> -
/// duplicated here rather than referenced, matching how <c>EtlPipelines.Samples.Tests</c> already
/// keeps its own copy too, so no test project depends on another one's internals. Declared with
/// <c>[ClassDataSource&lt;T&gt;(Shared = SharedType.PerAssembly)]</c> the container is started once
/// for the whole test project rather than once per test class.
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
/// A genuinely empty Postgres container - nothing has run <c>_Init.sql</c>
/// against it yet, which is exactly the state <see cref="ServerDatabaseDockerTests"/> needs to
/// prove dbdeploy's scripts create a schema <see cref="ServerDbContext"/>'s mapping actually
/// agrees with.
/// </summary>
public sealed class PostgreSqlFixture : DatabaseFixture<PostgreSqlContainer>
{
    // Same image tag EtlPipelines.Samples.Tests pins its own Postgres fixture to - see that
    // project's ContainerImages.cs for why this is pinned at all rather than left to
    // Testcontainers' own default.
    private const string Image = "postgres:15.1";

    // Named to match db/dbsettings.json's own "server" database, so the container is
    // already the right database, empty, the moment it starts - dbdeploy only ever needs to
    // create tables in it, never the database itself, which sidesteps needing --create at all.
    protected override PostgreSqlContainer CreateContainer() =>
        new PostgreSqlBuilder(Image)
            .WithDatabase("etlpipelines_server")
            .Build();
}
