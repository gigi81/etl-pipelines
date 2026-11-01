using System.Text.Json.Serialization;

namespace EtlPipelines.Server.Contracts;

/// <summary>
/// A configuration entry the pipelines can read, as the configuration page lists it: its key and when
/// it last changed. Never its value - values are secrets, encrypted at rest, and are only ever
/// handed to a pipeline process that is running.
/// </summary>
/// <param name="Key">The colon-path key, e.g. <c>ConnectionStrings:sales</c>.</param>
/// <param name="UpdatedAtUtc">When it was last set.</param>
public sealed record ConfigurationKeyDto(
    string Key,
    [property: JsonConverter(typeof(UtcDateTimeConverter))] DateTime UpdatedAtUtc);
