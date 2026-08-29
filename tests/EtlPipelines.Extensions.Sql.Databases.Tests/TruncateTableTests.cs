using EtlPipelines.Abstractions.Building;
using EtlPipelines.Core;
using EtlPipelines.Extensions.Sql;
using EtlPipelines.Extensions.Sql.MySql;
using EtlPipelines.Extensions.Sql.Oracle;
using EtlPipelines.Extensions.Sql.PostgreSql;
using EtlPipelines.Extensions.Sql.SqlServer;
using EtlPipelines.Extensions.Sql.Statements;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Extensions.Sql.Databases.Tests;

/// <summary>
/// Proves that <c>TruncateTable</c>'s default statement - real <c>TRUNCATE TABLE</c> - is syntax each
/// of these engines actually accepts.
/// </summary>
/// <remarks>
/// The SQLite suite proves the plumbing: a connection with no <see cref="ITruncateStatement"/> of its
/// own falls back to <see cref="TruncateTableStatement"/>, and one with its own override - SQLite's -
/// gets that instead. What it cannot prove is that the fallback's literal text, <c>TRUNCATE TABLE
/// {table}</c>, is something SQL Server, PostgreSQL, MySQL and Oracle will actually run as given, with
/// no engine-specific qualifier. That is only settled against the real thing.
/// </remarks>
[Category("Docker")]
public abstract class TruncateTableTests<TFixture>
    where TFixture : IDatabaseFixture
{
    private const string Pipeline = "maintenance";
    private const string Connection = "warehouse";

    protected TruncateTableTests(TFixture fixture) => Fixture = fixture;

    protected TFixture Fixture { get; }

    /// <summary>Registers this engine's connection under the name the pipeline uses.</summary>
    protected abstract Action<IServiceCollection, string> Register { get; }

    /// <summary>DDL for a one-column table, which every engine spells differently.</summary>
    protected abstract string CreateTableSql(string table);

    private static string NewName(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    private ServiceProvider Build(Action<IPipelineBuilder> build)
    {
        var services = new ServiceCollection();
        Register(services, Connection);
        services.AddEtlPipeline(Pipeline, build);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Empties_a_table_using_the_engines_own_TRUNCATE_statement()
    {
        //arrange
        var table = NewName("orders");

        await using var provider = Build(builder => builder.TruncateTable(Connection, table));

        await using (var setup = await provider.GetRequiredDbConnectionFactory(Connection).OpenAsync(CancellationToken.None))
        {
            await using var create = setup.CreateCommand();
            create.CommandText = CreateTableSql(table);
            await create.ExecuteNonQueryAsync();

            await using var insert = setup.CreateCommand();
            insert.CommandText = $"INSERT INTO {table} (Id) VALUES (1)";
            await insert.ExecuteNonQueryAsync();
        }

        //act
        var result = await provider.GetRequiredEtlPipeline(Pipeline).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);

        await using var connection = await provider.GetRequiredDbConnectionFactory(Connection).OpenAsync(CancellationToken.None);
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {table}";
        Convert.ToInt64(await count.ExecuteScalarAsync())
            .Should().Be(0, "TruncateTable must actually empty the table on this engine");
    }
}

[InheritsTests]
[ClassDataSource<SqlServerFixture>(Shared = SharedType.PerAssembly)]
public sealed class SqlServerTruncateTableTests(SqlServerFixture fixture)
    : TruncateTableTests<SqlServerFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddSqlServerConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) => $"CREATE TABLE {table} (Id INT)";
}

[InheritsTests]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class PostgreSqlTruncateTableTests(PostgreSqlFixture fixture)
    : TruncateTableTests<PostgreSqlFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddPostgreSqlConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) => $"CREATE TABLE {table} (Id INT)";
}

[InheritsTests]
[ClassDataSource<MySqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class MySqlTruncateTableTests(MySqlFixture fixture)
    : TruncateTableTests<MySqlFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddMySqlConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) => $"CREATE TABLE {table} (Id INT)";
}

[InheritsTests]
[ClassDataSource<OracleFixture>(Shared = SharedType.PerAssembly)]
public sealed class OracleTruncateTableTests(OracleFixture fixture)
    : TruncateTableTests<OracleFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddOracleConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) => $"CREATE TABLE {table} (Id NUMBER(10))";
}
