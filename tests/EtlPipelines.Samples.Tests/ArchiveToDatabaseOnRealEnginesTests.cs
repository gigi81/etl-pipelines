using System.Data.Common;
using EtlPipelines.Samples.ArchiveToDatabase.Stages;
using EtlPipelines.Samples.Tests.Fixtures;
using EtlPipelines.Extensions.Sql.MySql;
using EtlPipelines.Extensions.Sql.Oracle;
using EtlPipelines.Extensions.Sql.PostgreSql;
using EtlPipelines.Extensions.Sql.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.Tests;

/// <summary>
/// The zip-and-load sample, run unchanged against each engine it claims to support.
/// </summary>
/// <remarks>
/// Same claim as <see cref="CsvToDatabaseOnRealEnginesTests{TFixture}"/>, for the sample that loads
/// into five tables instead of one: the pipeline only names its database, so the only difference
/// between engines is which <c>Add…Connection</c> registered that name. Reuses the fixtures
/// <see cref="CsvToDatabaseOnRealEnginesTests{TFixture}"/> already declares — <c>Shared =
/// SharedType.PerAssembly</c> means the same running container serves both, so this adds no extra
/// container startup cost, and the table names never collide with <c>trades</c>.
/// </remarks>
[Category("Docker")]
[Category("Samples")]
public abstract class ArchiveToDatabaseOnRealEnginesTests<TFixture>
    where TFixture : IDatabaseFixture
{
    protected ArchiveToDatabaseOnRealEnginesTests(TFixture fixture) => Fixture = fixture;

    protected TFixture Fixture { get; }

    /// <summary>Registers this engine's connection under the name the sample loads into.</summary>
    protected abstract Action<IServiceCollection, string> Register { get; }

    /// <summary>Opens a connection directly, for creating the tables and counting the rows after.</summary>
    protected abstract Func<CancellationToken, ValueTask<DbConnection>> Open();

    /// <summary>The <c>CREATE TABLE</c> for one of the sample's five tables, in this engine's dialect.</summary>
    protected abstract string CreateTableSql(string table);

    [Test]
    public async Task Loads_the_sample_s_five_tables_into_the_engine()
    {
        //arrange
        // The engine is the only thing that changes: the same registration the sample's own command
        // line uses, handed a different Add...Connection. Handing it one is what tells the real job
        // not to create its own tables, so creating them here is the whole of the arrangement - the
        // feed itself is built by the sample's own setup pipeline below.
        await using var scratch = new SampleScratch("archive-db", (services, directory) =>
        {
            ArchiveToDatabase.Pipeline.AddPipeline(services, directory, false);
            Register(services, ArchiveToDatabase.Pipeline.Connection);
        });

        await using (var connection = await Open()(CancellationToken.None))
        {
            foreach (var table in ArchiveToDatabase.Pipeline.Tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = CreateTableSql(table);
                await command.ExecuteNonQueryAsync();
            }
        }

        //act
        var seeded = await scratch.RunAsync(ArchiveToDatabase.Pipeline.SeedName);

        //assert - the vendor's side builds the feed regardless of which engine the job loads into
        seeded.IsError.Should().BeFalse(seeded.IsError ? seeded.FirstError.Description : null);

        //act
        var loaded = await scratch.RunAsync(ArchiveToDatabase.Pipeline.Name);

        //assert
        loaded.IsError.Should().BeFalse(loaded.IsError ? loaded.FirstError.Description : null);

        await using var check = await Open()(CancellationToken.None);
        foreach (var table in ArchiveToDatabase.Pipeline.Tables)
        {
            await using var count = check.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table}";
            Convert.ToInt64(await count.ExecuteScalarAsync())
                .Should().Be(SeedStage.RowsPerFile, $"{table} should hold every row its CSV carried");
        }
    }
}

[InheritsTests]
[ClassDataSource<SqlServerFixture>(Shared = SharedType.PerAssembly)]
public sealed class SqlServerArchiveTests(SqlServerFixture fixture)
    : ArchiveToDatabaseOnRealEnginesTests<SqlServerFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        SqlServerExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddSqlServerConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Name NVARCHAR(50))";
}

[InheritsTests]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class PostgreSqlArchiveTests(PostgreSqlFixture fixture)
    : ArchiveToDatabaseOnRealEnginesTests<PostgreSqlFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        PostgreSqlExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddPostgreSqlConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Name TEXT)";
}

[InheritsTests]
[ClassDataSource<MySqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class MySqlArchiveTests(MySqlFixture fixture)
    : ArchiveToDatabaseOnRealEnginesTests<MySqlFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        MySqlExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddMySqlConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Name VARCHAR(50))";
}

[InheritsTests]
[ClassDataSource<MariaDbFixture>(Shared = SharedType.PerAssembly)]
public sealed class MariaDbArchiveTests(MariaDbFixture fixture)
    : ArchiveToDatabaseOnRealEnginesTests<MariaDbFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        MySqlExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddMySqlConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Name VARCHAR(50))";
}

[InheritsTests]
[ClassDataSource<OracleFixture>(Shared = SharedType.PerAssembly)]
public sealed class OracleArchiveTests(OracleFixture fixture)
    : ArchiveToDatabaseOnRealEnginesTests<OracleFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        OracleExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddOracleConnection(name, Fixture.ConnectionString);

    // Oracle DDL can't share a batch with other statements, which is why each table is created with
    // its own ExecuteNonQueryAsync call above rather than one multi-statement command text.
    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id NUMBER(10), Name VARCHAR2(50))";
}
