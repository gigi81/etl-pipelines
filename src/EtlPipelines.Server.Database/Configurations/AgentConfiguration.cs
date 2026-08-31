using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="Agent"/> onto the "Agents" table.</summary>
internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("Agents");
        builder.HasKey(agent => agent.Id);

        builder.Property(agent => agent.MachineName).IsRequired();

        // A primitive collection, not a joined table - EF Core 8+ maps List<string>/
        // IReadOnlyList<string> natively: Npgsql as a real "text[]" column (what the Deploy
        // script declares), other providers (SQLite, in the fast tests) as JSON. Either way,
        // no separate AgentTags table or manual serialization is needed.
        builder.Property(agent => agent.Tags).IsRequired();

        builder.Property(agent => agent.Version).IsRequired();

        builder.Property(agent => agent.Status)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(agent => agent.LastHeartbeatAt).IsRequired();
    }
}
