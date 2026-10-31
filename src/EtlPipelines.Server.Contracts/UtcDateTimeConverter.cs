using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtlPipelines.Server.Contracts;

/// <summary>
/// Reads and writes a <see cref="DateTime"/> as an ISO 8601 string that is always UTC (ending in
/// <c>Z</c>), whatever <see cref="DateTime.Kind"/> the value arrived with. Npgsql hands back
/// <see cref="DateTimeKind.Utc"/> for <c>timestamptz</c>, but other providers (SQLite) hand back
/// <see cref="DateTimeKind.Unspecified"/>, which would otherwise serialise without a <c>Z</c> and be
/// read by a browser as local time. Every timestamp field of the REST DTOs is named <c>...Utc</c> and
/// uses this, so no consumer has to guess the zone or convert.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    /// <summary>Treats an unspecified kind as UTC (what the database stores) and converts a local one.</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <inheritdoc />
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ToUtc(reader.GetDateTimeOffset().UtcDateTime);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToUtc(value));
}
