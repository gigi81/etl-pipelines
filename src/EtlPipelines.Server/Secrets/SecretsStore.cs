using System.Text;
using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Secrets;

/// <summary>
/// Wraps <see cref="IDataProtector"/> around <c>ConfigurationEntries</c> - the only place in this
/// project that ever touches a secret's plaintext, and only for as long as one
/// <see cref="SetAsync"/>/<see cref="GetAsync"/> call needs it. See SERVER.md's "Secrets at rest"
/// decision: this is explicitly not production-grade key management, swappable for a real KMS
/// later.
/// </summary>
public sealed class SecretsStore
{
    // A distinct purpose string, not the type name alone: IDataProtector's own guidance is that
    // the purpose identifies what the protected data is for, not what protects it - two different
    // features sharing a purpose could decrypt each other's data by accident.
    private const string Purpose = "EtlPipelines.Server.ConfigurationEntries.v1";

    private readonly ServerDbContext _context;
    private readonly IDataProtector _protector;

    public SecretsStore(ServerDbContext context, IDataProtectionProvider dataProtectionProvider)
    {
        _context = context;
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    /// <summary>Encrypts <paramref name="value"/> and upserts it under <paramref name="key"/>.</summary>
    /// <param name="key">The colon-path key, e.g. <c>"ConnectionStrings:sales"</c>.</param>
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(value));

        var existing = await _context.ConfigurationEntries
            .SingleOrDefaultAsync(entry => entry.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _context.ConfigurationEntries.Add(new ConfigurationEntry
            {
                Id = Guid.NewGuid(),
                Key = key,
                EncryptedValue = encrypted,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.EncryptedValue = encrypted;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads and decrypts the value stored under <paramref name="key"/>, or null if it has none.</summary>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var entry = await _context.ConfigurationEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Key == key, cancellationToken)
            .ConfigureAwait(false);

        return entry is null ? null : Encoding.UTF8.GetString(_protector.Unprotect(entry.EncryptedValue));
    }

    /// <summary>
    /// Reads and decrypts every entry - what <c>PipelineExecutionService.GetConfiguration</c>
    /// (SERVER.md Phase 6) hands a launched pipeline process, keyed exactly as
    /// <c>ConfigurationEntries.Key</c> already stores them, for
    /// <c>EtlPipelines.GrpcClient.GrpcConfigurationProvider</c> to materialize straight into
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> with no translation.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken)
    {
        var entries = await _context.ConfigurationEntries
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            result[entry.Key] = Encoding.UTF8.GetString(_protector.Unprotect(entry.EncryptedValue));
        }

        return result;
    }
}
