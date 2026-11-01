using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class Package
{
    public Guid Id { get; set; }

    public string NugetPackageId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<PackageVersion> PackageVersions { get; set; } = new List<PackageVersion>();

    public virtual ICollection<PipelineAgentTag> PipelineAgentTags { get; set; } = new List<PipelineAgentTag>();

    public virtual ICollection<PipelineAgent> PipelineAgents { get; set; } = new List<PipelineAgent>();

    public virtual ICollection<PipelineDisplayName> PipelineDisplayNames { get; set; } = new List<PipelineDisplayName>();
}
