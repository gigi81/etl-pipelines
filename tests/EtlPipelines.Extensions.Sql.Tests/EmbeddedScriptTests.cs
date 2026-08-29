using System.Reflection;
using EtlPipelines.Sql.Sqlite;

namespace EtlPipelines.Extensions.Sql.Tests;

/// <summary>
/// Running a script compiled into an assembly rather than deployed beside it.
/// </summary>
/// <remarks>
/// The scripts under <c>Scripts/</c> in this project are marked as EmbeddedResource, so these run
/// against real resources in this very assembly rather than a stand-in.
/// </remarks>
public sealed class EmbeddedScriptTests : IAsyncDisposable
{
    private const string Pipeline = "maintenance";
    private const string Connection = "orders";

    private const string FullName = "EtlPipelines.Extensions.Sql.Tests.Scripts.create-orders.sql";

    private static readonly Assembly Here = typeof(EmbeddedScriptTests).Assembly;

    private readonly SqliteHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private SqliteHost Registered() =>
        _host.Configure(services => services.AddSqliteConnection(Connection, _host.ConnectionString));

    [Test]
    [Arguments(FullName)]
    [Arguments("create-orders.sql")]
    public async Task Runs_a_script_embedded_in_an_assembly(string resourceName)
    {
        //arrange
        // Both the resource's full logical name and just its tail: almost nobody remembers that the
        // first is the root namespace and folder path joined with dots.
        Registered().AddEtlPipeline(Pipeline, b => b.RunEmbeddedSqlScript(Connection, Here, resourceName));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        (await _host.ScalarAsync<string>("SELECT Customer FROM orders")).Should().Be("embedded");
    }

    [Test]
    public async Task Names_the_stage_after_the_resource_that_was_asked_for()
    {
        //arrange
        Registered().AddEtlPipeline(Pipeline, b => b.RunEmbeddedSqlScript(Connection, Here, "create-orders.sql"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.Value.Stages.Should().ContainSingle().Which.Name.Should().Be("create-orders.sql");
    }

    [Test]
    public async Task Lists_what_the_assembly_holds_when_the_name_does_not_match()
    {
        //arrange
        // The failure everybody meets at least once, so the message answers the question it raises.
        Registered().AddEtlPipeline(Pipeline, b => b.RunEmbeddedSqlScript(Connection, Here, "no-such-script.sql"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        result.FirstError.Description.Should()
            .Contain("no-such-script.sql").And
            .Contain(FullName, "the message lists the resources that are there").And
            .Contain("EmbeddedResource", "and says what to check in the project file");
    }

    [Test]
    public async Task Refuses_to_guess_when_two_resources_end_the_same_way()
    {
        //arrange
        // Scripts/First/ambiguous.sql and Scripts/Second/ambiguous.sql both end in ".ambiguous.sql".
        Registered().AddEtlPipeline(Pipeline, b => b.RunEmbeddedSqlScript(Connection, Here, "ambiguous.sql"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue("picking one of the two would be a coin toss");
        result.FirstError.Description.Should()
            .Contain("First.ambiguous.sql").And
            .Contain("Second.ambiguous.sql");
    }

    [Test]
    public async Task A_missing_script_stops_the_run_before_the_stages_after_it()
    {
        //arrange
        Registered().AddEtlPipeline(Pipeline, b => b
            .RunEmbeddedSqlScript(Connection, Here, "no-such-script.sql")
            .RunEmbeddedSqlScript(Connection, Here, "create-orders.sql"));

        //act
        var result = await _host.RunAsync(Pipeline);

        //assert
        result.IsError.Should().BeTrue();
        (await _host.ScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'orders'"))
            .Should().Be(0, "the second script never ran");
    }
}
