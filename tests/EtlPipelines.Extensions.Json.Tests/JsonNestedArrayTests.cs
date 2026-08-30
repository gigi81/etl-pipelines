using System.IO.Abstractions;

namespace EtlPipelines.Extensions.Json.Tests;

/// <summary>
/// Reading a JSON array that is not the document's root value, via <see cref="JsonArraySourceOptions.Path"/>.
/// </summary>
public sealed class JsonNestedArrayTests
{
    private const string PipelineName = "orders";

    private readonly JsonTestHost _host = new();

    public sealed record Order(int Id, string Customer, decimal Amount);

    [Test]
    public async Task Reads_an_array_nested_one_level_deep()
    {
        //arrange
        var file = _host.File("envelope.json");
        await file.WriteAllTextAsync(
            """{"result":{"rows":[{"id":1,"customer":"acme","amount":10.5},{"id":2,"customer":"globex","amount":3.25}]}}""",
            CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["result", "rows"] }).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().Equal(new Order(1, "acme", 10.5m), new Order(2, "globex", 3.25m));
    }

    [Test]
    public async Task Reads_an_array_nested_three_levels_deep()
    {
        //arrange
        var file = _host.File("deep.json");
        await file.WriteAllTextAsync(
            """{"a":{"b":{"c":[{"id":42,"customer":"deep","amount":1}]}}}""",
            CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["a", "b", "c"] }).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().Equal(new Order(42, "deep", 1m));
    }

    [Test]
    public async Task Skips_over_sibling_properties_of_every_kind_while_walking_the_path()
    {
        //arrange
        // Number, string, object, array and null siblings both before and after the wrapper object,
        // and before and after the target array inside it - each is a different shape TrySkip has to
        // get through cleanly without touching the array we actually want.
        var file = _host.File("siblings.json");
        await file.WriteAllTextAsync(
            """
            {
              "before1": 123,
              "before2": "text",
              "before3": {"nested": {"more": [1, 2, 3]}},
              "before4": [1, 2, 3],
              "before5": null,
              "test": {
                "before": true,
                "rows": [{"id": 7, "customer": "x", "amount": 9.9}],
                "after": {"whatever": "value"}
              },
              "after1": false
            }
            """,
            CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["test", "rows"] }).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().Equal(new Order(7, "x", 9.9m));
    }

    [Test]
    public async Task Reads_an_empty_nested_array_as_no_rows()
    {
        //arrange
        var file = _host.File("empty.json");
        await file.WriteAllTextAsync("""{"result":{"rows":[]}}""", CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["result", "rows"] }).To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse("an empty array is not a failure");
        sink.Rows.Should().BeEmpty();
    }

    [Test]
    public async Task Streams_correctly_across_many_batch_boundaries()
    {
        //arrange
        var rows = string.Join(',', Enumerable.Range(0, 1_000)
            .Select(i => $$"""{"id":{{i}},"customer":"c{{i}}","amount":{{i}}.5}"""));
        var file = _host.File("many.json");
        await file.WriteAllTextAsync("""{"page":{"data":[""" + rows + "]}}", CancellationToken.None);

        var sink = new CollectingSink<Order>();
        _host.AddEtlPipeline(PipelineName, b => b
            .WithOptions(o => o.BatchSize = 32)
            .FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["page", "data"] })
            .To(sink));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().HaveCount(1_000);
        sink.Rows.Select(o => o.Id).Should().Equal(Enumerable.Range(0, 1_000));
        result.Value.RowsRead.Should().Be(1_000);
    }

    [Test]
    public async Task Fails_the_run_when_a_path_property_is_missing()
    {
        //arrange
        var file = _host.File("missing.json");
        await file.WriteAllTextAsync("""{"result":{"nope":[]}}""", CancellationToken.None);

        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["result", "rows"] })
                  .To(new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue("a path that does not exist in the document cannot be read");
    }

    [Test]
    public async Task Fails_the_run_when_the_path_does_not_lead_to_an_array()
    {
        //arrange
        var file = _host.File("not-an-array.json");
        await file.WriteAllTextAsync("""{"result":{"rows":"not an array"}}""", CancellationToken.None);

        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["result", "rows"] })
                  .To(new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();
    }

    [Test]
    public async Task Fails_the_run_when_an_intermediate_path_segment_is_not_an_object()
    {
        //arrange
        var file = _host.File("not-an-object.json");
        await file.WriteAllTextAsync("""{"result":"not an object"}""", CancellationToken.None);

        _host.AddEtlPipeline(
            PipelineName,
            b => b.FromJsonArray<Order>(file, new JsonArraySourceOptions { Path = ["result", "rows"] })
                  .To(new CollectingSink<Order>()));

        //act
        var result = await _host.RunAsync(PipelineName);

        //assert
        result.IsError.Should().BeTrue();
    }
}
