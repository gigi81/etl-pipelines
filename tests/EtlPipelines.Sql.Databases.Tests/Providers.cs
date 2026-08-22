using System.Data.Common;
using EtlPipelines.Sql.MySql;
using EtlPipelines.Sql.Oracle;
using EtlPipelines.Sql.PostgreSql;
using EtlPipelines.Sql.SqlServer;
using Testcontainers.MariaDb;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.Oracle;
using Testcontainers.PostgreSql;

namespace EtlPipelines.Sql.Databases.Tests;

public sealed class SqlServerFixture : DatabaseFixture<MsSqlContainer>
{
    protected override MsSqlContainer CreateContainer() =>
        new MsSqlBuilder(ContainerImages.SqlServer).Build();
}

[InheritsTests]
[ClassDataSource<SqlServerFixture>(Shared = SharedType.PerAssembly)]
public sealed class SqlServerBulkLoadTests(SqlServerFixture fixture) : BulkLoadTests<SqlServerFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        SqlServerExtensions.Open(Fixture.ConnectionString);

    protected override IBulkLoader BulkLoader => new SqlServerBulkLoader();

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Customer NVARCHAR(100), Amount DECIMAL(18,2))";
}

public sealed class PostgreSqlFixture : DatabaseFixture<PostgreSqlContainer>
{
    protected override PostgreSqlContainer CreateContainer() =>
        new PostgreSqlBuilder(ContainerImages.PostgreSql).Build();
}

[InheritsTests]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class PostgreSqlBulkLoadTests(PostgreSqlFixture fixture) : BulkLoadTests<PostgreSqlFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        PostgreSqlExtensions.Open(Fixture.ConnectionString);

    protected override IBulkLoader BulkLoader => new PostgreSqlBulkLoader();

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Customer TEXT, Amount NUMERIC(18,2))";
}

/// <summary>
/// MySqlBulkCopy is built on LOAD DATA LOCAL INFILE, which both ends refuse unless told otherwise —
/// hence AllowLoadLocalInfile on the connection string and local_infile on the server.
/// </summary>
public sealed class MySqlFixture : DatabaseFixture<MySqlContainer>
{
    protected override MySqlContainer CreateContainer() =>
        new MySqlBuilder(ContainerImages.MySql)
            .WithCommand("--local-infile=1")
            .Build();

    public override string ConnectionString =>
        $"{base.ConnectionString};AllowLoadLocalInfile=true";
}

[InheritsTests]
[ClassDataSource<MySqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class MySqlBulkLoadTests(MySqlFixture fixture) : BulkLoadTests<MySqlFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        MySqlExtensions.Open(Fixture.ConnectionString);

    protected override IBulkLoader BulkLoader => new MySqlBulkLoader();

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Customer VARCHAR(100), Amount DECIMAL(18,2))";
}

/// <summary>MariaDB through the MySQL connector, which is the claim worth testing.</summary>
public sealed class MariaDbFixture : DatabaseFixture<MariaDbContainer>
{
    protected override MariaDbContainer CreateContainer() =>
        new MariaDbBuilder(ContainerImages.MariaDb)
            .WithCommand("--local-infile=1")
            .Build();

    public override string ConnectionString =>
        $"{base.ConnectionString};AllowLoadLocalInfile=true";
}

[InheritsTests]
[ClassDataSource<MariaDbFixture>(Shared = SharedType.PerAssembly)]
public sealed class MariaDbBulkLoadTests(MariaDbFixture fixture) : BulkLoadTests<MariaDbFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        MySqlExtensions.Open(Fixture.ConnectionString);

    protected override IBulkLoader BulkLoader => new MySqlBulkLoader();

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id INT, Customer VARCHAR(100), Amount DECIMAL(18,2))";
}

public sealed class OracleFixture : DatabaseFixture<OracleContainer>
{
    protected override OracleContainer CreateContainer() =>
        new OracleBuilder(ContainerImages.Oracle).Build();
}

[InheritsTests]
[ClassDataSource<OracleFixture>(Shared = SharedType.PerAssembly)]
public sealed class OracleBulkLoadTests(OracleFixture fixture) : BulkLoadTests<OracleFixture>(fixture)
{
    protected override Func<CancellationToken, ValueTask<DbConnection>> Open() =>
        OracleExtensions.Open(Fixture.ConnectionString);

    protected override IBulkLoader BulkLoader => new OracleBulkLoader();

    /// <summary>Oracle binds with a colon, not an at sign.</summary>
    protected override string ParameterPrefix => ":";

    protected override string CreateTableSql(string table) =>
        $"CREATE TABLE {table} (Id NUMBER(10), Customer VARCHAR2(100), Amount NUMBER(18,2))";
}
