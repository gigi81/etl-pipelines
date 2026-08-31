using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="AgentResourceSample"/> onto the "AgentResourceSamples" table.</summary>
internal sealed class AgentResourceSampleConfiguration : IEntityTypeConfiguration<AgentResourceSample>
{
    public void Configure(EntityTypeBuilder<AgentResourceSample> builder)
    {
        builder.ToTable("AgentResourceSamples");
        builder.HasKey(sample => sample.Id);

        builder.Property(sample => sample.SampledAt).IsRequired();

        builder.HasOne<Run>()
            .WithMany()
            .HasForeignKey(sample => sample.RunId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(sample => sample.AgentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
