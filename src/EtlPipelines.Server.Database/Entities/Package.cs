using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class Package
{
    public Guid Id { get; set; }

    public string NugetPackageId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public virtual ICollection<PackageVersion> PackageVersions { get; set; } = new List<PackageVersion>();
}
