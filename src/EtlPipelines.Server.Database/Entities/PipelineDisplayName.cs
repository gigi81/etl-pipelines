using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class PipelineDisplayName
{
    public Guid PackageId { get; set; }

    public string PipelineName { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public DateTime UpdatedAt { get; set; }

    public virtual Package Package { get; set; } = null!;
}
