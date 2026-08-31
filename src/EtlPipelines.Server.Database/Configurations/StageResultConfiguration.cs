using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="StageResult"/> onto the "StageResults" table.</summary>
internal sealed class StageResultConfiguration : IEntityTypeConfiguration<StageResult>
{
    public void Configure(EntityTypeBuilder<StageResult> builder)
    {
        builder.ToTable("StageResults");
        builder.HasKey(result => result.Id);

        builder.Property(result => result.Name).IsRequired();

        builder.HasIndex(result => new { result.RunId, result.Sequence }).IsUnique();

        builder.HasOne<Run>()
            .WithMany()
            .HasForeignKey(result => result.RunId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
