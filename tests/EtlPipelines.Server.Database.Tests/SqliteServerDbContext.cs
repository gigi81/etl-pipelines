using EtlPipelines.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Database.Tests;

/// <summary>
/// A <see cref="ServerDbContext"/> backed by an in-memory SQLite database, created fresh via
/// <see cref="Database.EnsureCreated"/> - a convenience for exercising mapping/query logic
/// quickly, unrelated to how the real schema gets created and not a substitute for proving
/// <c>db</c>'s dbdeploy scripts are correct (see the <c>[Category("Docker")]</c> tests
/// for that).
/// </summary>
/// <remarks>
/// Owns the <see cref="SqliteConnection"/> it is built on and closes it on dispose. That
/// ownership is the whole reason this type exists rather than just calling
/// <c>UseSqlite("DataSource=:memory:")</c> inline: a SQLite <c>:memory:</c> database lives only as
/// long as its one connection stays open, and <c>DbContext</c> does not take ownership of a
/// connection handed to it via <c>UseSqlite(connection)</c> - something has to close it, and nothing
/// else in a test would.
/// </remarks>
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
