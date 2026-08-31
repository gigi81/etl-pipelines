using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class Pipeline
{
    public Guid Id { get; set; }

    public Guid PackageVersionId { get; set; }

    public string Name { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual PackageVersion PackageVersion { get; set; } = null!;

    public virtual ICollection<Run> Runs { get; set; } = new List<Run>();
}
