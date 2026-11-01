using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class PipelineAgent
{
    public Guid PackageId { get; set; }

    public string PipelineName { get; set; } = null!;

    public Guid AgentId { get; set; }

    public virtual Package Package { get; set; } = null!;
}
