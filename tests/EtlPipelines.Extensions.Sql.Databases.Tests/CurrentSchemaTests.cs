using System.Data.Common;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Core;
using EtlPipelines.Sql.MySql;
using EtlPipelines.Sql.Oracle;
using EtlPipelines.Sql.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace EtlPipelines.Extensions.Sql.Databases.Tests;

/// <summary>
/// Reading and writing unqualified names against a schema other than the connecting user's.
/// </summary>
/// <remarks>
/// Only a real server can answer this. The unit suite proves the right statement is produced; what
/// it cannot show is that the statement does what it is supposed to — that an unqualified INSERT
/// lands in the other schema, and that an unqualified SELECT finds it there.
/// </remarks>
[Category("Docker")]
public abstract class CurrentSchemaTests<TFixture>
    where TFixture : IDatabaseFixture
{
    private const string Connection = "warehouse";

    protected CurrentSchemaTests(TFixture fixture) => Fixture = fixture;

    protected TFixture Fixture { get; }

    /// <summary>The schema the tables live in, which is not the one the connection logs in as.</summary>
    protected const string OtherSchema = "hr";

    /// <summary>Registers the connection, pointed at <see cref="OtherSchema"/>.</summary>
    protected abstract Action<IServiceCollection, string> RegisterWithSchema { get; }

    /// <summary>Registers the same connection with nothing said about the schema.</summary>
    protected abstract Action<IServiceCollection, string> RegisterPlain { get; }

    /// <summary>
    /// Creates <see cref="OtherSchema"/> and a table in it, over a connection with the rights to.
    /// </summary>
    /// <remarks>
    /// The application user cannot do this on every engine — creating a user on Oracle needs a good
    /// deal more than a load job ever should have — so each engine says how it gets there.
    /// </remarks>
    protected abstract Task CreateSchemaAsync(string table);

    /// <summary>Counts rows in the table, naming the schema in full, over a plain connection.</summary>
    protected abstract Task<long> CountInOtherSchemaAsync(string table);

    protected static string NewTableName() => $"orders_{Guid.NewGuid():N}"[..20];

    public sealed record Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
    }

    private ServiceProvider Build(Action<IServiceCollection, string> register, Action<IPipelineBuilder> build)
    {
        var services = new ServiceCollection();
        register(services, Connection);
        services.AddEtlPipeline("job", build);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task An_unqualified_write_lands_in_the_schema_the_session_points_at()
    {
        //arrange
        var table = NewTableName();
        await CreateSchemaAsync(table);

        // The table name carries no schema, and the connecting user has no table by this name.
        await using var provider = Build(RegisterWithSchema, builder => builder
            .From(_ => new ArraySource<Order>([new Order { Id = 1, Customer = "a" }, new Order { Id = 2, Customer = "b" }]))
            .ToSqlTable(Connection, table));

        //act
        var result = await provider.GetRequiredEtlPipeline("job").RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await CountInOtherSchemaAsync(table))
            .Should().Be(2, "the rows went to the other schema, not the connecting user's");
    }

    [Test]
    public async Task An_unqualified_read_resolves_against_it_too()
    {
        //arrange
        var table = NewTableName();
        await CreateSchemaAsync(table);

        await using var loader = Build(RegisterWithSchema, builder => builder
            .From(_ => new ArraySource<Order>([new Order { Id = 1, Customer = "a" }]))
            .ToSqlTable(Connection, table));

        (await loader.GetRequiredEtlPipeline("job").RunAsync(CancellationToken.None))
            .IsError.Should().BeFalse();

        var rows = new CollectingSink<Order>();

        // The SELECT is the caller's own SQL, which nothing could have qualified on their behalf -
        // which is the whole reason a session setting is needed rather than a table prefix.
        await using var reader = Build(RegisterWithSchema, builder => builder
            .FromSql<Order>(Connection, $"SELECT Id, Customer FROM {table}")
            .To(_ => rows));

        //act
        var result = await reader.GetRequiredEtlPipeline("job").RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        rows.Rows.Should().ContainSingle().Which.Customer.Should().Be("a");
    }

    [Test]
    public async Task Without_it_the_same_pipeline_cannot_find_the_table()
    {
        //arrange
        // The control. Without this the tests above would pass just as well if the tables had been
        // created in the connecting user's own schema all along.
        var table = NewTableName();
        await CreateSchemaAsync(table);

        await using var provider = Build(RegisterPlain, builder => builder
            .From(_ => new ArraySource<Order>([new Order { Id = 1, Customer = "a" }]))
            .ToSqlTable(Connection, table));

        //act
        var result = await provider.GetRequiredEtlPipeline("job").RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue("the table is not in the schema this connection resolves against");
    }
}

[InheritsTests]
[ClassDataSource<OracleFixture>(Shared = SharedType.PerAssembly)]
public sealed class OracleCurrentSchemaTests(OracleFixture fixture) : CurrentSchemaTests<OracleFixture>(fixture)
{
    protected override Action<IServiceCollection, string> RegisterWithSchema =>
        (services, name) => services.AddOracleConnection(
            name, Unpooled, options => options.CurrentSchema = OtherSchema);

    protected override Action<IServiceCollection, string> RegisterPlain =>
        (services, name) => services.AddOracleConnection(name, Unpooled);

    /// <summary>
    /// The same database, with pooling off.
    /// </summary>
    /// <remarks>
    /// ODP.NET does not reset session state when a connection goes back to the pool, so a connection
    /// this class set CURRENT_SCHEMA on would be handed to the next caller of the same connection
    /// string still pointed at the other schema — and the other test classes here share it. That is
    /// not a quirk of the tests: it is the documented hazard of the feature, and turning pooling off
    /// is one of the two ways out of it. The other is a connection string of its own.
    /// </remarks>
    private string Unpooled =>
        new OracleConnectionStringBuilder(Fixture.ConnectionString) { Pooling = false }.ConnectionString;

    /// <summary>
    /// A schema is a user here, and creating one needs rights the load user has no business holding —
    /// so the setup goes in as SYSTEM, whose password Testcontainers sets to the one it configured.
    /// </summary>
    private string ElevatedConnectionString =>
        new OracleConnectionStringBuilder(Fixture.ConnectionString) { UserID = "system" }.ConnectionString;

    private string LoadUser => new OracleConnectionStringBuilder(Fixture.ConnectionString).UserID;

    protected override async Task CreateSchemaAsync(string table)
    {
        await using var connection = new OracleConnection(ElevatedConnectionString);
        await connection.OpenAsync();

        // Created once and reused; the tables inside it are what carry a unique name per test.
        await TryAsync(connection, $"CREATE USER {OtherSchema} IDENTIFIED BY {OtherSchema} QUOTA UNLIMITED ON USERS");
        await ExecuteAsync(connection, $"CREATE TABLE {OtherSchema}.{table} (Id NUMBER(10), Customer VARCHAR2(100))");
        await ExecuteAsync(connection, $"GRANT SELECT, INSERT ON {OtherSchema}.{table} TO {LoadUser}");
    }

    protected override async Task<long> CountInOtherSchemaAsync(string table)
    {
        await using var connection = new OracleConnection(ElevatedConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {OtherSchema}.{table}";

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>For the CREATE USER, which the second test in this class finds already done.</summary>
    private static async Task TryAsync(DbConnection connection, string sql)
    {
        try
        {
            await ExecuteAsync(connection, sql);
        }
        catch (DbException)
        {
            // Already there.
        }
    }
}

[InheritsTests]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class PostgreSqlCurrentSchemaTests(PostgreSqlFixture fixture)
    : CurrentSchemaTests<PostgreSqlFixture>(fixture)
{
    protected override Action<IServiceCollection, string> RegisterWithSchema =>
        (services, name) => services.AddPostgreSqlConnection(
            name, Fixture.ConnectionString, options => options.CurrentSchema = OtherSchema);

    protected override Action<IServiceCollection, string> RegisterPlain =>
        (services, name) => services.AddPostgreSqlConnection(name, Fixture.ConnectionString);

    /// <summary>The connecting role owns the database, so no elevation is needed.</summary>
    protected override async Task CreateSchemaAsync(string table)
    {
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync();

        // In two statements, because CREATE SCHEMA IF NOT EXISTS is not atomic in PostgreSQL: two of
        // these tests running at once both find the schema missing and both try to create it, and
        // the loser gets a unique violation on pg_namespace rather than the silence IF NOT EXISTS
        // promises. The table name is unique per test, so only the schema needs the guard.
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {OtherSchema}");
        }
        catch (PostgresException exception) when (exception.SqlState is "23505" or "42P06")
        {
            // The other test won.
        }

        await ExecuteAsync(connection, $"CREATE TABLE {OtherSchema}.{table} (Id INT, Customer TEXT)");
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    protected override async Task<long> CountInOtherSchemaAsync(string table)
    {
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {OtherSchema}.{table}";

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}

[InheritsTests]
[ClassDataSource<MySqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class MySqlCurrentSchemaTests(MySqlFixture fixture) : CurrentSchemaTests<MySqlFixture>(fixture)
{
    protected override Action<IServiceCollection, string> RegisterWithSchema =>
        (services, name) => services.AddMySqlConnection(
            name, Fixture.ConnectionString, options => options.CurrentSchema = OtherSchema);

    protected override Action<IServiceCollection, string> RegisterPlain =>
        (services, name) => services.AddMySqlConnection(name, Fixture.ConnectionString);

    /// <summary>A schema is a database here, and creating one goes in as root.</summary>
    private string ElevatedConnectionString =>
        new MySqlConnectionStringBuilder(Fixture.ConnectionString) { UserID = "root" }.ConnectionString;

    private string LoadUser => new MySqlConnectionStringBuilder(Fixture.ConnectionString).UserID;

    protected override async Task CreateSchemaAsync(string table)
    {
        await using var connection = new MySqlConnection(ElevatedConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE DATABASE IF NOT EXISTS {OtherSchema}; " +
            $"CREATE TABLE {OtherSchema}.{table} (Id INT, Customer VARCHAR(100)); " +
            $"GRANT ALL ON {OtherSchema}.* TO '{LoadUser}'@'%'";

        await command.ExecuteNonQueryAsync();
    }

    protected override async Task<long> CountInOtherSchemaAsync(string table)
    {
        await using var connection = new MySqlConnection(ElevatedConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {OtherSchema}.{table}";

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
