using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="Run"/> onto the "Runs" table. <see cref="Run.Id"/> is the session id - see that type's own remarks.</summary>
internal sealed class RunConfiguration : IEntityTypeConfiguration<Run>
{
    public void Configure(EntityTypeBuilder<Run> builder)
    {
        builder.ToTable("Runs");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.Status)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(run => run.RequestedAt).IsRequired();

        builder.HasOne<Pipeline>()
            .WithMany()
            .HasForeignKey(run => run.PipelineId)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable FK: a run is queued before any agent claims it (Run.AgentId's own remarks).
        builder.HasOne<Agent>()
            .WithMany()
            .HasForeignKey(run => run.AgentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
