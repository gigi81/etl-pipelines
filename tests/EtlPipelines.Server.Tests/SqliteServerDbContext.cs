using EtlPipelines.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Tests;

/// <summary>
/// A <see cref="ServerDbContext"/> backed by a fresh in-memory SQLite database - the same pattern
/// <c>EtlPipelines.Server.Database.Tests</c> uses for its own fast tests (duplicated here rather
/// than shared, matching how this repo's test projects generally keep their own copies of small
/// fixtures). Owns the underlying <see cref="SqliteConnection"/> and closes it on dispose - see
/// that project's version of this type for why that ownership is the whole reason it exists.
/// </summary>
internal sealed class SqliteServerDbContext : ServerDbContext
{
    private readonly SqliteConnection _connection;

    private SqliteServerDbContext(DbContextOptions<ServerDbContext> options, SqliteConnection connection)
        : base(options)
    {
        _connection = connection;
    }

    public static SqliteServerDbContext Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new SqliteServerDbContext(options, connection);
        context.Database.EnsureCreated();
        return context;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _connection.Dispose();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
