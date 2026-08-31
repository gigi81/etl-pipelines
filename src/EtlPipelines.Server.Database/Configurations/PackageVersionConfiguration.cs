using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="PackageVersion"/> onto the "PackageVersions" table.</summary>
internal sealed class PackageVersionConfiguration : IEntityTypeConfiguration<PackageVersion>
{
    public void Configure(EntityTypeBuilder<PackageVersion> builder)
    {
        builder.ToTable("PackageVersions");
        builder.HasKey(version => version.Id);

        builder.Property(version => version.Version).IsRequired();
        builder.Property(version => version.InstalledAt).IsRequired();

        // Stored as text (Installing/Installed/Failed), not an integer - readable straight out of
        // psql without a lookup table, the same choice every status column in this schema makes.
        builder.Property(version => version.Status)
            .HasConversion<string>()
            .IsRequired();

        builder.HasIndex(version => new { version.PackageId, version.Version }).IsUnique();

        // No navigation properties anywhere in this project, deliberately - a query/mapping layer
        // over dbdeploy's tables has no need for the collection-navigation/lazy-loading surface
        // area a richer domain model would, and every relationship the schema itself expresses is
        // already right here as a foreign key scalar (PackageId).
        builder.HasOne<Package>()
            .WithMany()
            .HasForeignKey(version => version.PackageId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
