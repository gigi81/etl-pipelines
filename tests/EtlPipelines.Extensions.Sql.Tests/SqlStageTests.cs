using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using EtlPipelines.Extensions.Sql.Sqlite;

namespace EtlPipelines.Extensions.Sql.Tests;

/// <summary>
/// Running a script file and a stored procedure as stages of a pipeline.
/// </summary>
/// <remarks>
/// SQLite carries the contract here, as it does for the source and the sink: it needs no container
/// and so runs on every CI leg. What is specific to an engine is how a file is split into batches,
/// which is the script parsers' business and tested separately.
/// </remarks>
public sealed class SqlStageTests : IAsyncDisposable
{
    private const string Pipeline = "maintenance";
    private const string Connection = "orders";

    private readonly SqliteHost _host = new();
    private readonly MockFileSystem _files = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private IFileInfo Script(string sql)
    {
        var path = _files.Path.Combine(
            _files.Directory.GetCurrentDirectory(),
            $"{Guid.NewGuid():N}.sql");

        _files.AddFile(path, new MockFileData(sql));

        return _files.FileInfo.New(path);
    }

    private SqliteHost Registered() =>
        _host.Configure(services => services.AddSqliteConnection(Connection, _host.ConnectionString));

    [Test]
    public async Task Runs_a_script_file_as_a_stage()
    {
        //arrange
        var script = Script("""
            CREATE TABLE orders (Id INTEGER, Amount NUMERIC);
            INSERT INTO orders (Id, Amount) VALUES (1, 10), (2, 20);
            """);

        Registered().AddEtlPipeline(Pipeline, b => b.RunSqlScript(Connection, script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(2);
    }

    [Test]
    public async Task Names_the_script_stage_after_its_file()
    {
        //arrange
        var script = Script("CREATE TABLE orders (Id INTEGER)");
        Registered().AddEtlPipeline(Pipeline, b => b.RunSqlScript(Connection, script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be(script.Name);
    }

    [Test]
    public async Task Takes_a_name_for_the_script_stage_when_one_is_given()
    {
        //arrange
        var script = Script("CREATE TABLE orders (Id INTEGER)");

        Registered().AddEtlPipeline(Pipeline, b => b
            .RunSqlScript(Connection, script, options => options.Name = "create the tables"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be("create the tables");
    }

    [Test]
    public async Task Moves_no_rows_so_the_run_still_reports_the_ones_that_did()
    {
        //arrange
        // A script or a procedure hands work to the server rather than pulling rows through here, so
        // it must not be what the run's row counts are taken from.
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");
        var script = Script("DELETE FROM orders WHERE Id < 0");

        Registered().AddEtlPipeline(Pipeline, b => b
            .RunSqlScript(Connection, script)
            .From(_ => new ArraySource<Order>(Sample(3)))
            .ToSqlTable(Connection, "orders"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        result.Value.Stages.Should().HaveCount(2);
        result.Value.RowsRead.Should().Be(3, "the script stage moved none, so it is skipped");
        result.Value.RowsWritten.Should().Be(3);
    }

    [Test]
    public async Task Says_which_batch_of_a_script_failed()
    {
        //arrange
        var script = Script("""
            CREATE TABLE orders (Id INTEGER);
            SELECT * FROM a_table_that_is_not_there;
            """);

        Registered().AddEtlPipeline(Pipeline, b => b.RunSqlScript(Connection, script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should()
            .Contain(script.Name).And
            .Contain("batch 1", "SQLite takes the whole file as one batch");
    }

    [Test]
    public async Task Says_so_when_the_script_is_not_there()
    {
        //arrange
        var missing = _files.FileInfo.New("nowhere.sql");
        Registered().AddEtlPipeline(Pipeline, b => b.RunSqlScript(Connection, missing));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("does not exist");
    }

    [Test]
    public async Task A_failing_script_stops_the_run_before_the_stages_after_it()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)");
        var script = Script("SELECT * FROM a_table_that_is_not_there");

        Registered().AddEtlPipeline(Pipeline, b => b
            .RunSqlScript(Connection, script)
            .From(_ => new ArraySource<Order>(Sample(3)))
            .ToSqlTable(Connection, "orders"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders"))
            .Should().Be(0, "the load never ran");
    }

    [Test]
    public async Task Runs_a_script_an_earlier_stage_wrote()
    {
        //arrange
        // The reason the stage refreshes the file rather than trusting it: IFileInfo caches what it
        // found when it was built, and here that was before the file existed. A job that fetches its
        // script and then runs it is an ordinary shape.
        var path = _files.Path.Combine(_files.Directory.GetCurrentDirectory(), "fetched.sql");
        var script = _files.FileInfo.New(path);

        Registered().AddEtlPipeline(Pipeline, b => b
            .AddStage("fetch", (_, _) =>
            {
                _files.AddFile(path, new MockFileData("CREATE TABLE orders (Id INTEGER)"));
                return ValueTask.FromResult<ErrorOr<Success>>(Result.Success);
            })
            .RunSqlScript(Connection, script));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(0, "the table exists");
    }

    [Test]
    public async Task Runs_one_statement_as_a_stage()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER)");
        await _host.ExecuteAsync("INSERT INTO orders (Id) VALUES (1), (2), (3)");

        Registered().AddEtlPipeline(Pipeline, b => b.RunSql(Connection, "DELETE FROM orders WHERE Id > 1"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(1);
    }

    [Test]
    public async Task Binds_parameters_rather_than_pasting_them_into_the_statement()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER, Customer TEXT)");

        // The value is one an injection would love: pasting it into the string would end the
        // statement early and run whatever came next.
        const string awkward = "O'Brien'); DROP TABLE orders; --";

        Registered().AddEtlPipeline(Pipeline, b => b
            .RunSql(Connection, "INSERT INTO orders (Id, Customer) VALUES (1, $name)", options =>
                options.Configure = command =>
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "$name";
                    parameter.Value = awkward;
                    command.Parameters.Add(parameter);
                }));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<string>("SELECT Customer FROM orders")).Should().Be(awkward);
    }

    [Test]
    public async Task Names_a_statement_stage_after_the_statement()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER)");
        Registered().AddEtlPipeline(Pipeline, b => b.RunSql(Connection, "DELETE  FROM\n  orders"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        // Collapsed to one line, because a run's report is a list and a statement is not.
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be("DELETE FROM orders");
    }

    [Test]
    public async Task Shortens_a_long_statement_down_to_a_stage_name()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER)");
        var sql = $"DELETE FROM orders WHERE Id IN ({string.Join(", ", Enumerable.Range(1, 40))})";

        Registered().AddEtlPipeline(Pipeline, b => b.RunSql(Connection, sql));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        var name = result.Value.Stages.Should().ContainSingle().Subject.Name;
        name.Should().HaveLength(40).And.EndWith("...").And.StartWith("DELETE FROM orders");
    }

    [Test]
    public async Task Reports_a_statement_that_will_not_run()
    {
        //arrange
        Registered().AddEtlPipeline(Pipeline, b => b.RunSql(Connection, "DELETE FROM not_a_table"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should().Contain("DELETE FROM not_a_table");
    }

    [Test]
    public async Task Truncates_a_table_as_a_stage()
    {
        //arrange
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER)");
        await _host.ExecuteAsync("INSERT INTO orders (Id) VALUES (1), (2), (3)");

        Registered().AddEtlPipeline(Pipeline, b => b.TruncateTable(Connection, "orders"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<long>("SELECT COUNT(*) FROM orders")).Should().Be(0);
    }

    [Test]
    public async Task Truncating_an_already_empty_table_is_not_an_error()
    {
        //arrange
        // SQLite has no TRUNCATE statement at all - this is the test that would catch TruncateTable
        // reaching for one anyway.
        await _host.ExecuteAsync("CREATE TABLE orders (Id INTEGER)");
        Registered().AddEtlPipeline(Pipeline, b => b.TruncateTable(Connection, "orders"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
    }

    public sealed record Order
    {
        public long Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    private static Order[] Sample(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => new Order
        {
            Id = i,
            Customer = $"customer-{i}",
            Amount = i * 1.5m,
        }),
    ];
}
