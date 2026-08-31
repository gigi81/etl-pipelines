using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="ConfigurationEntry"/> onto the "ConfigurationEntries" table.</summary>
internal sealed class ConfigurationEntryConfiguration : IEntityTypeConfiguration<ConfigurationEntry>
{
    public void Configure(EntityTypeBuilder<ConfigurationEntry> builder)
    {
        builder.ToTable("ConfigurationEntries");
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Key).IsRequired();
        builder.HasIndex(entry => entry.Key).IsUnique();

        builder.Property(entry => entry.EncryptedValue).IsRequired();
        builder.Property(entry => entry.UpdatedAt).IsRequired();
    }
}
