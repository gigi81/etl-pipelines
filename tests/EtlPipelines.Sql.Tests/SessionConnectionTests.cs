using System.Data.Common;
using EtlPipelines.Sql.MySql;
using EtlPipelines.Sql.Oracle;
using EtlPipelines.Sql.PostgreSql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Sql.Tests;

/// <summary>
/// Statements run on a connection as it opens, and the named settings built on them.
/// </summary>
/// <remarks>
/// What a session setting actually does to an engine is only answerable by that engine, and the
/// container suite asks it. What is answerable here is everything around it: that the statements run
/// at all, in order, that a connection whose setup failed is not handed out, and that each provider
/// turns its <c>CurrentSchema</c> into the statement that engine understands.
/// </remarks>
public sealed class SessionConnectionTests : IAsyncDisposable
{
    private readonly SqliteHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    /// <summary>A factory over a real SQLite connection, so statements have something to run against.</summary>
    private sealed class SqliteFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public SqliteFactory(string connectionString) => _connectionString = connectionString;

        public int Opened { get; private set; }

        public string Name => "test";

        public async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken)
        {
            Opened++;

            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            return connection;
        }
    }

    [Test]
    public async Task Runs_its_statements_on_the_connection_it_hands_back()
    {
        //arrange
        var inner = new SqliteFactory(_host.ConnectionString);
        var factory = new SessionDbConnectionFactory(inner, ["CREATE TABLE a (Id INTEGER)"]);

        //act
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        //assert
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM a";
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0, "the table exists");
    }

    [Test]
    public async Task Runs_them_in_the_order_they_were_given()
    {
        //arrange
        // The second depends on the first, so the wrong order is a failure rather than a wrong answer.
        var factory = new SessionDbConnectionFactory(
            new SqliteFactory(_host.ConnectionString),
            ["CREATE TABLE a (Id INTEGER)", "INSERT INTO a (Id) VALUES (1)"]);

        //act
        await using var connection = await factory.OpenAsync(CancellationToken.None);

        //assert
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM a";
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1);
    }

    [Test]
    public async Task Runs_them_again_for_every_connection()
    {
        //arrange
        // The whole reason this is a factory concern: session state rides the physical connection,
        // and a pool hands those back out.
        var inner = new SqliteFactory(_host.ConnectionString);
        var factory = new SessionDbConnectionFactory(inner, ["PRAGMA foreign_keys = ON"]);

        //act
        await (await factory.OpenAsync(CancellationToken.None)).DisposeAsync();
        await (await factory.OpenAsync(CancellationToken.None)).DisposeAsync();

        //assert
        inner.Opened.Should().Be(2);
    }

    [Test]
    public async Task Does_not_hand_back_a_connection_whose_setup_failed()
    {
        //arrange
        var factory = new SessionDbConnectionFactory(
            new SqliteFactory(_host.ConnectionString),
            ["SELECT * FROM a_table_that_is_not_there"]);

        //act
        var act = async () => await factory.OpenAsync(CancellationToken.None);

        //assert
        // Handing it back would run the caller's work against the wrong session and say nothing.
        await act.Should().ThrowAsync<SqliteException>();
    }

    [Test]
    public async Task A_connection_registered_without_statements_is_not_wrapped()
    {
        //arrange
        var services = new ServiceCollection();
        services.AddSqliteConnection("plain", _host.ConnectionString);

        //act
        var factory = services.BuildServiceProvider().GetRequiredDbConnectionFactory("plain");

        //assert
        factory.Should().BeOfType<DelegateDbConnectionFactory>("the common case pays nothing");
    }

    [Test]
    public async Task A_connection_registered_with_statements_runs_them()
    {
        //arrange
        var services = new ServiceCollection();
        services.AddSqliteConnection("seeded", _host.ConnectionString, options =>
            options.SessionStatements.Add("CREATE TABLE IF NOT EXISTS a (Id INTEGER)"));

        //act
        await using var connection = await services.BuildServiceProvider()
            .GetRequiredDbConnectionFactory("seeded")
            .OpenAsync(CancellationToken.None);

        //assert
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM a";
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);
    }

    [Test]
    [Arguments("oracle", "ALTER SESSION SET CURRENT_SCHEMA = HR")]
    [Arguments("postgres", "SET search_path TO hr")]
    [Arguments("mysql", "USE hr")]
    public void Each_engine_asks_for_its_schema_the_way_that_engine_understands(string engine, string expected)
    {
        //arrange
        var services = new ServiceCollection();

        //act
        // Registered with a connection string so nothing needs configuration, and never opened: what
        // is being checked is the statement, which is the part that differs per engine.
        switch (engine)
        {
            case "oracle":
                services.AddOracleConnection(engine, "User Id=x;Password=y;Data Source=z",
                    options => options.CurrentSchema = "HR");
                break;
            case "postgres":
                services.AddPostgreSqlConnection(engine, "Host=x;Database=y",
                    options => options.CurrentSchema = "hr");
                break;
            default:
                services.AddMySqlConnection(engine, "Server=x;Database=y",
                    options => options.CurrentSchema = "hr");
                break;
        }

        //assert
        services.BuildServiceProvider()
            .GetRequiredDbConnectionFactory(engine)
            .Should().BeOfType<SessionDbConnectionFactory>()
            .Which.SessionStatements.Should().Equal([expected]);
    }

    [Test]
    public void Says_nothing_to_the_session_when_no_schema_was_asked_for()
    {
        //arrange
        var services = new ServiceCollection();
        services.AddOracleConnection("plain", "User Id=x;Password=y;Data Source=z");

        //act
        var factory = services.BuildServiceProvider().GetRequiredDbConnectionFactory("plain");

        //assert
        factory.Should().BeOfType<DelegateDbConnectionFactory>();
    }

    [Test]
    [Arguments("hr; DROP TABLE orders")]
    [Arguments("hr'")]
    [Arguments("hr schema")]
    [Arguments("1hr")]
    [Arguments("\"hr\"")]
    public void Refuses_a_schema_name_that_is_not_an_identifier(string schema)
    {
        //arrange
        // It is written into the statement rather than bound, and it often comes from configuration.
        var services = new ServiceCollection();

        //act
        var act = () => services.AddOracleConnection(
            "warehouse", "User Id=x;Password=y;Data Source=z", options => options.CurrentSchema = schema);

        //assert
        act.Should().Throw<ArgumentException>().WithMessage("*not a valid identifier*");
    }

    [Test]
    [Arguments("HR")]
    [Arguments("hr_staging")]
    [Arguments("HR$1")]
    [Arguments("hr#2")]
    public void Accepts_the_identifiers_every_engine_here_takes_unquoted(string schema)
    {
        //arrange
        var services = new ServiceCollection();

        //act
        var act = () => services.AddOracleConnection(
            "warehouse", "User Id=x;Password=y;Data Source=z", options => options.CurrentSchema = schema);

        //assert
        act.Should().NotThrow();
    }
}
