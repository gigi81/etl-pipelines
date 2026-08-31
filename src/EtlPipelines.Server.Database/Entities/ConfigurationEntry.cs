namespace EtlPipelines.Server.Database.Entities;

/// <summary>
/// One flat configuration key, mirroring <c>IConfiguration</c>'s own colon-path shape
/// deliberately (e.g. <c>"ConnectionStrings:sales"</c>) - what lets
/// <c>GetConfigurationResponse.entries</c> (pipeline_execution.v1) feed straight into a
/// <c>ConfigurationProvider</c> with zero translation (Phase 6).
/// </summary>
/// <remarks>
/// <see cref="EncryptedValue"/> is never plaintext - Phase 4's <c>SecretsStore</c> wraps
/// <c>IDataProtector</c> around every read and write; nothing in this project decrypts it.
/// </remarks>
public sealed class ConfigurationEntry
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }
    public required byte[] EncryptedValue { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
