using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class PackageVersion
{
    public Guid Id { get; set; }

    public Guid PackageId { get; set; }

    public string Version { get; set; } = null!;

    public DateTime InstalledAt { get; set; }

    public string Status { get; set; } = null!;

    public virtual Package Package { get; set; } = null!;

    public virtual ICollection<Pipeline> Pipelines { get; set; } = new List<Pipeline>();
}
