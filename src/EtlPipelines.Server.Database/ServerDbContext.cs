using System;
using System.Collections.Generic;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Database;

public partial class ServerDbContext : DbContext
{
    public ServerDbContext(DbContextOptions<ServerDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Agent> Agents { get; set; }

    public virtual DbSet<AgentResourceSample> AgentResourceSamples { get; set; }

    public virtual DbSet<ConfigurationEntry> ConfigurationEntries { get; set; }

    public virtual DbSet<NuGetFeed> NuGetFeeds { get; set; }

    public virtual DbSet<Package> Packages { get; set; }

    public virtual DbSet<PackageVersion> PackageVersions { get; set; }

    public virtual DbSet<Pipeline> Pipelines { get; set; }

    public virtual DbSet<Run> Runs { get; set; }

    public virtual DbSet<StageResult> StageResults { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Agents_pkey");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Tags).HasDefaultValueSql("'{}'::text[]");
        });

        modelBuilder.Entity<AgentResourceSample>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("AgentResourceSamples_pkey");

            entity.HasIndex(e => e.AgentId, "IX_AgentResourceSamples_AgentId");

            entity.HasIndex(e => e.RunId, "IX_AgentResourceSamples_RunId");

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.Agent).WithMany(p => p.AgentResourceSamples)
                .HasForeignKey(d => d.AgentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AgentResourceSamples_Agents");

            entity.HasOne(d => d.Run).WithMany(p => p.AgentResourceSamples)
                .HasForeignKey(d => d.RunId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_AgentResourceSamples_Runs");
        });

        modelBuilder.Entity<ConfigurationEntry>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("ConfigurationEntries_pkey");

            entity.HasIndex(e => e.Key, "UX_ConfigurationEntries_Key").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<NuGetFeed>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("NuGetFeeds_pkey");

            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<Package>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Packages_pkey");

            entity.HasIndex(e => e.NugetPackageId, "UX_Packages_NugetPackageId").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<PackageVersion>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PackageVersions_pkey");

            entity.HasIndex(e => e.PackageId, "IX_PackageVersions_PackageId");

            entity.HasIndex(e => new { e.PackageId, e.Version }, "UX_PackageVersions_PackageId_Version").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.Package).WithMany(p => p.PackageVersions)
                .HasForeignKey(d => d.PackageId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_PackageVersions_Packages");
        });

        modelBuilder.Entity<Pipeline>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Pipelines_pkey");

            entity.HasIndex(e => e.PackageVersionId, "IX_Pipelines_PackageVersionId");

            entity.HasIndex(e => new { e.PackageVersionId, e.Name }, "UX_Pipelines_PackageVersionId_Name").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.PackageVersion).WithMany(p => p.Pipelines)
                .HasForeignKey(d => d.PackageVersionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Pipelines_PackageVersions");
        });

        modelBuilder.Entity<Run>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("Runs_pkey");

            entity.HasIndex(e => e.AgentId, "IX_Runs_AgentId");

            entity.HasIndex(e => e.PipelineId, "IX_Runs_PipelineId");

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.Agent).WithMany(p => p.Runs)
                .HasForeignKey(d => d.AgentId)
                .HasConstraintName("FK_Runs_Agents");

            entity.HasOne(d => d.Pipeline).WithMany(p => p.Runs)
                .HasForeignKey(d => d.PipelineId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Runs_Pipelines");
        });

        modelBuilder.Entity<StageResult>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("StageResults_pkey");

            entity.HasIndex(e => e.RunId, "IX_StageResults_RunId");

            entity.HasIndex(e => new { e.RunId, e.Sequence }, "UX_StageResults_RunId_Sequence").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.Run).WithMany(p => p.StageResults)
                .HasForeignKey(d => d.RunId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_StageResults_Runs");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
