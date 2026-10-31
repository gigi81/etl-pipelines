using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Server.Tests.Live;

/// <summary>
/// An in-memory SQLite database behind a real <see cref="IServiceScopeFactory"/> - what the sync
/// service takes a scope from for each batch. The connection stays open for the object's lifetime (an
/// in-memory SQLite database disappears when its connection closes).
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    private TestDatabase(SqliteConnection connection, ServiceProvider provider)
    {
        _connection = connection;
        _provider = provider;
    }

    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    public static async Task<TestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ServerDbContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.EnsureCreatedAsync();
        }

        return new TestDatabase(connection, provider);
    }

    /// <summary>Runs <paramref name="work"/> against a fresh context.</summary>
    public async Task<T> ReadAsync<T>(Func<ServerDbContext, Task<T>> work)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ServerDbContext>());
    }

    /// <summary>Seeds an installed pipeline (package, version, pipeline rows) and returns its id.</summary>
    public async Task<Guid> SeedPipelineAsync(string name = "p")
    {
        return await ReadAsync(async context =>
        {
            var package = new Package { Id = Guid.NewGuid(), NugetPackageId = "Pkg", CreatedAt = DateTime.UtcNow };
            var version = new PackageVersion { Id = Guid.NewGuid(), PackageId = package.Id, Version = "1.0.0", InstalledAt = DateTime.UtcNow, Status = "Installed" };
            var pipeline = new Pipeline { Id = Guid.NewGuid(), PackageVersionId = version.Id, Name = name, CreatedAt = DateTime.UtcNow };
            context.AddRange(package, version, pipeline);
            await context.SaveChangesAsync();
            return pipeline.Id;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
