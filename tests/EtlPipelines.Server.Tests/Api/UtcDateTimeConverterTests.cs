using System.Text.Json;
using EtlPipelines.Server.Contracts;

namespace EtlPipelines.Server.Tests.Api;

/// <summary><see cref="UtcDateTimeConverter"/>: a timestamp on the wire is UTC, whatever kind it started as.</summary>
[Category("Server")]
public class UtcDateTimeConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new UtcDateTimeConverter() } };

    [Test]
    public async Task An_unspecified_kind_is_taken_as_UTC()
    {
        var json = JsonSerializer.Serialize(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), Options);

        json.Should().Be("\"2026-01-02T03:04:05Z\"");
        await Task.CompletedTask;
    }

    [Test]
    public async Task A_local_time_is_converted_to_UTC()
    {
        var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToLocalTime();

        var json = JsonSerializer.Serialize(local, Options);

        json.Should().Be("\"2026-01-02T03:04:05Z\"");
        await Task.CompletedTask;
    }

    [Test]
    public async Task A_value_with_an_offset_is_read_back_as_the_same_UTC_instant()
    {
        var value = JsonSerializer.Deserialize<DateTime>("\"2026-01-02T05:04:05+02:00\"", Options);

        value.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        value.Kind.Should().Be(DateTimeKind.Utc);
        await Task.CompletedTask;
    }

    [Test]
    public async Task A_nullable_timestamp_round_trips_and_null_stays_null()
    {
        var dto = new RunSummaryDto(
            Guid.NewGuid(), Guid.NewGuid(), "p", "pkg", "1.0", null, null, "Running",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), null, null, null, null, null, null, 0, null);

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"requestedAtUtc\":\"2026-01-02T03:04:05Z\"");
        json.Should().Contain("\"startedAtUtc\":null");
        await Task.CompletedTask;
    }
}
