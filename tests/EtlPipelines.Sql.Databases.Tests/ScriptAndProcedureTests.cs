using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Core;
using EtlPipelines.Sql.MySql;
using EtlPipelines.Sql.Oracle;
using EtlPipelines.Sql.PostgreSql;
using EtlPipelines.Sql.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Sql.Databases.Tests;

/// <summary>
/// Running a script file and a stored procedure against each real engine.
/// </summary>
/// <remarks>
/// The splitting itself is unit-tested without a container. What only a real server settles is
/// whether the batches that come out are ones it will actually accept — a procedure body is the
/// case that breaks, because it is where SQL Server needs its own batch, MySQL needs its delimiter
/// changed and Oracle needs its <c>END;</c> left alone.
/// </remarks>
[Category("Docker")]
public abstract class ScriptAndProcedureTests<TFixture>
    where TFixture : IDatabaseFixture
{
    private const string Pipeline = "maintenance";
    private const string Connection = "warehouse";

    private readonly MockFileSystem _files = new();

    protected ScriptAndProcedureTests(TFixture fixture) => Fixture = fixture;

    protected TFixture Fixture { get; }

    /// <summary>Registers this engine's connection under the name the pipeline uses.</summary>
    protected abstract Action<IServiceCollection, string> Register { get; }

    /// <summary>
    /// A script that creates <paramref name="table"/>, puts two rows in it, and defines a procedure
    /// named <paramref name="procedure"/> that deletes the row with Id 1.
    /// </summary>
    /// <remarks>Every engine spells all three differently, which is the point of the exercise.</remarks>
    protected abstract string SetupScript(string table, string procedure);

    /// <summary>How this engine's procedure is called.</summary>
    protected virtual string CallSyntax(string procedure) => procedure;

    private IFileInfo Script(string sql)
    {
        var path = _files.Path.Combine(_files.Directory.GetCurrentDirectory(), $"{Guid.NewGuid():N}.sql");
        _files.AddFile(path, new MockFileData(sql));
        return _files.FileInfo.New(path);
    }

    private static string NewName(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    private ServiceProvider Build(Action<IPipelineBuilder> build)
    {
        var services = new ServiceCollection();
        Register(services, Connection);
        services.AddEtlPipeline(Pipeline, build);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Runs_a_script_that_defines_a_procedure_then_calls_it()
    {
        //arrange
        var table = NewName("orders");
        var procedure = NewName("prune");
        var script = Script(SetupScript(table, procedure));

        await using var provider = Build(builder => builder
            .RunSqlScript(Connection, script)
            .RunStoredProcedure(Connection, CallSyntax(procedure)));

        //act
        var result = await provider.GetRequiredEtlPipeline(Pipeline).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.Stages.Select(stage => stage.Name).Should().Equal([script.Name, CallSyntax(procedure)]);

        await using var connection = await provider
            .GetRequiredDbConnectionFactory(Connection)
            .OpenAsync(CancellationToken.None);

        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM {table}";
        Convert.ToInt64(await count.ExecuteScalarAsync())
            .Should().Be(1, "the procedure deleted one of the two rows the script inserted");
    }

    [Test]
    public async Task Reports_a_procedure_that_is_not_there()
    {
        //arrange
        await using var provider = Build(builder =>
            builder.RunStoredProcedure(Connection, "no_such_procedure_at_all"));

        //act
        var result = await provider.GetRequiredEtlPipeline(Pipeline).RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("no_such_procedure_at_all");
    }
}

[InheritsTests]
[ClassDataSource<SqlServerFixture>(Shared = SharedType.PerAssembly)]
public sealed class SqlServerScriptTests(SqlServerFixture fixture)
    : ScriptAndProcedureTests<SqlServerFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddSqlServerConnection(name, Fixture.ConnectionString);

    /// <summary>CREATE PROCEDURE has to be the first statement of its batch, so GO earns its keep.</summary>
    protected override string SetupScript(string table, string procedure) => $"""
        CREATE TABLE {table} (Id INT, Amount DECIMAL(18,2));
        INSERT INTO {table} VALUES (1, 10), (2, 20);
        GO
        CREATE PROCEDURE {procedure} AS
        BEGIN
            DELETE FROM {table} WHERE Id = 1;
        END
        GO
        """;
}

[InheritsTests]
[ClassDataSource<PostgreSqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class PostgreSqlScriptTests(PostgreSqlFixture fixture)
    : ScriptAndProcedureTests<PostgreSqlFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddPostgreSqlConnection(name, Fixture.ConnectionString);

    /// <summary>The whole file in one command, dollar-quoted body and all.</summary>
    protected override string SetupScript(string table, string procedure) => $"""
        CREATE TABLE {table} (Id INT, Amount NUMERIC(18,2));
        INSERT INTO {table} VALUES (1, 10), (2, 20);
        CREATE PROCEDURE {procedure}() LANGUAGE SQL AS $$
            DELETE FROM {table} WHERE Id = 1;
        $$;
        """;
}

[InheritsTests]
[ClassDataSource<MySqlFixture>(Shared = SharedType.PerAssembly)]
public sealed class MySqlScriptTests(MySqlFixture fixture)
    : ScriptAndProcedureTests<MySqlFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddMySqlConnection(name, Fixture.ConnectionString);

    /// <summary>The body is full of semicolons, so the script changes its own delimiter.</summary>
    protected override string SetupScript(string table, string procedure) => $"""
        CREATE TABLE {table} (Id INT, Amount DECIMAL(18,2));
        INSERT INTO {table} VALUES (1, 10), (2, 20);
        DELIMITER $$
        CREATE PROCEDURE {procedure}()
        BEGIN
            DELETE FROM {table} WHERE Id = 1;
        END$$
        DELIMITER ;
        """;
}

[InheritsTests]
[ClassDataSource<OracleFixture>(Shared = SharedType.PerAssembly)]
public sealed class OracleScriptTests(OracleFixture fixture)
    : ScriptAndProcedureTests<OracleFixture>(fixture)
{
    protected override Action<IServiceCollection, string> Register =>
        (services, name) => services.AddOracleConnection(name, Fixture.ConnectionString);

    /// <summary>Terminated with / throughout, which is what tells the parser this is PL/SQL.</summary>
    protected override string SetupScript(string table, string procedure) => $"""
        CREATE TABLE {table} (Id NUMBER(10), Amount NUMBER(18,2))
        /
        INSERT INTO {table} VALUES (1, 10)
        /
        INSERT INTO {table} VALUES (2, 20)
        /
        CREATE OR REPLACE PROCEDURE {procedure} AS
        BEGIN
            DELETE FROM {table} WHERE Id = 1;
        END;
        /
        """;
}
