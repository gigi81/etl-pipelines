using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class PipelineAgentTag
{
    public Guid PackageId { get; set; }

    public string PipelineName { get; set; } = null!;

    public string Tag { get; set; } = null!;

    public virtual Package Package { get; set; } = null!;
}
