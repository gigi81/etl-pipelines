using System.Data.Common;
using EtlPipelines.Samples.Common;
using EtlPipelines.Samples.CsvToDatabase;
using EtlPipelines.Sql.MySql;
using EtlPipelines.Sql.Oracle;
using EtlPipelines.Sql.PostgreSql;
using EtlPipelines.Sql.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.Oracle;
using Testcontainers.PostgreSql;

namespace EtlPipelines.Samples.Tests;

/// <summary>
/// The CSV-to-database sample, run unchanged against each engine it claims to support.
/// </summary>
/// <remarks>
/// The sample names its database and nothing else, which is the claim being tested: the same
/// pipeline loads a file into any of these engines, and the only difference is which
/// <c>Add…Connection</c> registered that name. Running the sample itself rather than a copy of it is
/// what makes this evidence.
/// </remarks>
[Category("Docker")]
[Category("Samples")]
public abstract class CsvToDatabaseOnRealEnginesTests<TFixture>
    where TFixture : IDatabaseFixture
{
    protected CsvToDatabaseOnRealEnginesTests(TFixture fixture) => Fixture = fixture;

    protected TFixture Fixture { get; }

    /// <summary>Registers this engine's connection under the name the sample loads into.</summary>
    protected abstract Action<IServiceCollection, string> Register { get; }

    /// <summary>Opens a connection directly, for creating the table and counting the rows after.</summary>
    protected abstract Func<CancellationToken, ValueTask<DbConnection>> Open();

    protected abstract string CreateTableSql { get; }

    protected virtual string? ParameterPrefix => null;

    [Test]
    public async Task Loads_the_sample_file_into_the_table()
    {
        //arrange
        using var scratch = new SampleScratch("csv-db");

        await using (var connection = await Open()(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = CreateTableSql;
            await command.ExecuteNonQueryAsync();
        }

        //act
        // The engine is the only thing that changes: the same sample class, given a connection, a
        // bulk loader and a bind marker, and run through the same host its Program.cs starts.
        await using var run = await SampleHost.RunAsync(
            new CsvToDatabaseSample
            {
                ConfigureConnection = Register,
                ParameterPrefix = ParameterPrefix,
            },
            scratch.Workspace);

        //assert
        run.Result.RowsRead.Should().Be(10_000);
        run.Result.RowsWritten.Should().Be(CsvToDatabaseSample.ExpectedRows);

        await using var check = await Open()(CancellationToken.None);
        await using var count = check.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {CsvToDatabaseSample.TableName}";
        Convert.ToInt64(await count.ExecuteScalarAsync())
            .Should().Be(CsvToDatabaseSample.ExpectedRows, "every filtered row reached the table");
    }
}

public sealed class SqlServerFixture : DatabaseFixture<MsSqlContainer>
{
    protected override MsSqlContainer CreateContainer() =>
        new MsSqlBuilder(ContainerImages.SqlServer).Build();
}

[InheritsTests]
[ClassDataSource<SqlServerFixture>(Shared = SharedType.PerAssembly)]
public sealed class SqlServerSampleTests(SqlServerFixture fixture)
    : CsvToDatabaseOnRealEnginesTests<SqlServerFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        SqlServerExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddSqlServerConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql =>
        $"CREATE TABLE {CsvToDatabaseSample.TableName} (Id INT, Symbol NVARCHAR(10), Price DECIMAL(18,2), Quantity INT)";
}

public sealed class PostgreSqlFixture : DatabaseFixture<PostgreSqlContainer>
{
    protected override PostgreSqlContainer CreateContainer() =>
        new PostgreSqlBuilder(ContainerImages.PostgreSql).Build();
}

[InheritsTests]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class PostgreSqlSampleTests(PostgreSqlFixture fixture)
    : CsvToDatabaseOnRealEnginesTests<PostgreSqlFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        PostgreSqlExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddPostgreSqlConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql =>
        $"CREATE TABLE {CsvToDatabaseSample.TableName} (Id INT, Symbol TEXT, Price NUMERIC(18,2), Quantity INT)";
}

public sealed class MySqlFixture : DatabaseFixture<MySqlContainer>
{
    protected override MySqlContainer CreateContainer() =>
        new MySqlBuilder(ContainerImages.MySql).WithCommand("--local-infile=1").Build();

    public override string ConnectionString => $"{base.ConnectionString};AllowLoadLocalInfile=true";
}

[InheritsTests]
[ClassDataSource<MySqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class MySqlSampleTests(MySqlFixture fixture)
    : CsvToDatabaseOnRealEnginesTests<MySqlFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        MySqlExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddMySqlConnection(name, Fixture.ConnectionString);

    protected override string CreateTableSql =>
        $"CREATE TABLE {CsvToDatabaseSample.TableName} (Id INT, Symbol VARCHAR(10), Price DECIMAL(18,2), Quantity INT)";
}

public sealed class OracleFixture : DatabaseFixture<OracleContainer>
{
    protected override OracleContainer CreateContainer() =>
        new OracleBuilder(ContainerImages.Oracle).Build();
}

[InheritsTests]
[ClassDataSource<OracleFixture>(Shared = SharedType.PerAssembly)]
public sealed class OracleSampleTests(OracleFixture fixture)
    : CsvToDatabaseOnRealEnginesTests<OracleFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        OracleExtensions.Open(Fixture.ConnectionString);

    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddOracleConnection(name, Fixture.ConnectionString);

    /// <summary>Oracle binds with a colon, not an at sign.</summary>
    protected override string? ParameterPrefix => ":";

    protected override string CreateTableSql =>
        $"CREATE TABLE {CsvToDatabaseSample.TableName} (Id NUMBER(10), Symbol VARCHAR2(10), Price NUMBER(18,2), Quantity NUMBER(10))";
}
