using System.Data;
using System.Data.Common;
using EtlPipelines.Abstractions.Ports;
using EtlPipelines.Core.Sources;
using EtlPipelines.Core.Tests.Fixtures;

namespace EtlPipelines.Core.Tests;

/// <summary>Bridging an ADO.NET reader into a pipeline.</summary>
public class DataReaderSourceTests
{
    private const string PipelineName = "people";

    private sealed record Person(int Id, string Name);

    private static Person Map(IDataRecord record) => new(record.GetInt32(0), record.GetString(1));

    private static DataTable PeopleTable(int count)
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));

        for (var i = 0; i < count; i++)
        {
            table.Rows.Add(i, $"person-{i}");
        }

        return table;
    }

    [Test]
    public async Task Streams_every_row_through_a_pipeline()
    {
        //arrange
        // 250 rows against a 32-row batch: the cursor must be resumed across batch boundaries rather
        // than restarted or abandoned.
        using var table = PeopleTable(250);
        var sink = new InMemorySink<Person>();

        //act
        var result = await EtlPipeline.CreateBuilder(PipelineName)
            .WithOptions(o => o.BatchSize = 32)
            .From<Person>(new DataReaderSource<Person>(_ => new ValueTask<IDataReader>(table.CreateDataReader()), Map))
            .To(sink)
            .Build()
            .RunAsync(CancellationToken.None);

        //assert
        result.IsError.Should().BeFalse(result.IsError ? result.FirstError.Description : null);
        sink.Rows.Should().HaveCount(250);
        sink.Rows.Select(p => p.Id).Should().Equal(Enumerable.Range(0, 250));
        sink.Rows[0].Name.Should().Be("person-0");
    }

    [Test]
    public async Task Fills_the_buffer_then_reports_zero_at_the_end()
    {
        //arrange
        using var table = PeopleTable(5);
        await using var source = new DataReaderSource<Person>(table.CreateDataReader(), Map);
        await source.InitializeAsync(CancellationToken.None);

        var buffer = new Person[3];

        //act
        // Five rows through a three-row buffer, read one more time than there is data for.
        var reads = new[]
        {
            (await source.ReadAsync(buffer, CancellationToken.None)).Value,
            (await source.ReadAsync(buffer, CancellationToken.None)).Value,
            (await source.ReadAsync(buffer, CancellationToken.None)).Value,
            (await source.ReadAsync(buffer, CancellationToken.None)).Value,
        };

        //assert
        reads[0].Should().Be(3, "a full buffer must be filled");
        reads[1].Should().Be(2, "a short read is not end of data");
        reads[2].Should().Be(0, "zero is the end-of-data signal");
        reads[3].Should().Be(0, "and it stays zero once latched");
    }

    [Test]
    public async Task Uses_the_asynchronous_path_when_the_provider_offers_one()
    {
        //arrange
        using var table = PeopleTable(4);
        using var reader = table.CreateDataReader();

        // Every real provider's reader derives from DbDataReader, which is what lets the source avoid
        // blocking a thread-pool thread on each round trip.
        reader.Should().BeAssignableTo<DbDataReader>();

        await using var source = new DataReaderSource<Person>(reader, Map);
        await source.InitializeAsync(CancellationToken.None);

        var buffer = new Person[4];

        //act
        var read = await source.ReadAsync(buffer, CancellationToken.None);

        //assert
        read.Value.Should().Be(4);
    }

    [Test]
    public async Task Falls_back_to_the_synchronous_path_for_a_plain_IDataReader()
    {
        //arrange
        var reader = new SyncOnlyDataReader([(1, "a"), (2, "b"), (3, "c")]);
        reader.Should().NotBeAssignableTo<DbDataReader>();

        await using var source = new DataReaderSource<Person>(reader, Map);
        await source.InitializeAsync(CancellationToken.None);

        var buffer = new Person[8];

        //act
        var read = await source.ReadAsync(buffer, CancellationToken.None);

        //assert
        read.Value.Should().Be(3);
        reader.SyncReads.Should().Be(4, "three rows plus the read that reports the end");
        buffer[..3].Select(p => p.Name).Should().Equal("a", "b", "c");
    }

    [Test]
    public async Task Materialises_each_row_rather_than_buffering_the_cursor()
    {
        //arrange
        // The trap this source exists to prevent: IDataRecord is one object repositioned per row, so
        // a batch built from the record itself would hold N views of the last row read.
        using var table = PeopleTable(4);
        await using var source = new DataReaderSource<Person>(table.CreateDataReader(), Map);
        await source.InitializeAsync(CancellationToken.None);

        var buffer = new Person[4];

        //act
        await source.ReadAsync(buffer, CancellationToken.None);

        //assert
        buffer.Select(p => p.Id).Should().Equal(0, 1, 2, 3);
        buffer.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task Opens_its_reader_per_run_rather_than_at_construction()
    {
        //arrange
        using var table = PeopleTable(3);
        var opens = 0;

        var source = new DataReaderSource<Person>(
            _ =>
            {
                opens++;
                return new ValueTask<IDataReader>(table.CreateDataReader());
            },
            Map);

        //act
        var opensBeforeInitialize = opens;
        await source.InitializeAsync(CancellationToken.None);

        //assert
        opensBeforeInitialize.Should().Be(0, "constructing the source must not execute the query");
        opens.Should().Be(1);

        await source.DisposeAsync();
    }

    [Test]
    public async Task Reports_a_clear_error_when_used_before_initialization()
    {
        //arrange
        using var table = PeopleTable(1);
        var source = new DataReaderSource<Person>(_ => new ValueTask<IDataReader>(table.CreateDataReader()), Map);

        //act
        var read = await source.ReadAsync(new Person[1], CancellationToken.None);

        //assert
        read.IsError.Should().BeTrue();
        read.FirstError.Code.Should().Be("datareader.not_initialized");
    }

    [Test]
    public async Task Closes_the_reader_it_owns_and_leaves_a_borrowed_one_alone()
    {
        //arrange
        var owned = new SyncOnlyDataReader([(1, "a")]);
        var borrowed = new SyncOnlyDataReader([(1, "a")]);

        //act
        await using (var source = new DataReaderSource<Person>(owned, Map))
        {
            await source.InitializeAsync(CancellationToken.None);
        }

        await using (var source = new DataReaderSource<Person>(borrowed, Map, leaveOpen: true))
        {
            await source.InitializeAsync(CancellationToken.None);
        }

        //assert
        owned.IsClosed.Should().BeTrue("the source owns a reader passed without leaveOpen");
        borrowed.IsClosed.Should().BeFalse("leaveOpen hands ownership back to the caller");
    }
}
