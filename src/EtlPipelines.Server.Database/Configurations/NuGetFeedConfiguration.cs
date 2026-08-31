using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtlPipelines.Server.Database.Configurations;

/// <summary>Maps <see cref="NuGetFeed"/> onto the "NuGetFeeds" table.</summary>
internal sealed class NuGetFeedConfiguration : IEntityTypeConfiguration<NuGetFeed>
{
    public void Configure(EntityTypeBuilder<NuGetFeed> builder)
    {
        builder.ToTable("NuGetFeeds");
        builder.HasKey(feed => feed.Id);

        builder.Property(feed => feed.Url).IsRequired();
        builder.Property(feed => feed.Ordinal).IsRequired();
    }
}
