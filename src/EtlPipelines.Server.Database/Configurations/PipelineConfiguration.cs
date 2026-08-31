using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="Pipeline"/> onto the "Pipelines" table.</summary>
internal sealed class PipelineConfiguration : IEntityTypeConfiguration<Pipeline>
{
    public void Configure(EntityTypeBuilder<Pipeline> builder)
    {
        builder.ToTable("Pipelines");
        builder.HasKey(pipeline => pipeline.Id);

        builder.Property(pipeline => pipeline.Name).IsRequired();
        builder.Property(pipeline => pipeline.CreatedAt).IsRequired();

        builder.HasIndex(pipeline => new { pipeline.PackageVersionId, pipeline.Name }).IsUnique();

        builder.HasOne<PackageVersion>()
            .WithMany()
            .HasForeignKey(pipeline => pipeline.PackageVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
