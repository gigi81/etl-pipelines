namespace EtlPipelines.Server.Contracts;

/// <summary>The body of <c>PUT /api/configuration</c>: one entry to create or replace.</summary>
/// <param name="Key">The colon-path key, e.g. <c>ConnectionStrings:sales</c>.</param>
/// <param name="Value">The value. Stored encrypted; never returned.</param>
public sealed record ConfigurationEntryDto(string Key, string Value);
