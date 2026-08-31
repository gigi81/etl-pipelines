using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="Package"/> onto the "Packages" table <c>InitialSchema.CreateCoreTables.Deploy.sql</c> creates.</summary>
internal sealed class PackageConfiguration : IEntityTypeConfiguration<Package>
{
    public void Configure(EntityTypeBuilder<Package> builder)
    {
        builder.ToTable("Packages");
        builder.HasKey(package => package.Id);

        builder.Property(package => package.NugetPackageId).IsRequired();
        builder.HasIndex(package => package.NugetPackageId).IsUnique();

        builder.Property(package => package.CreatedAt).IsRequired();
    }
}
