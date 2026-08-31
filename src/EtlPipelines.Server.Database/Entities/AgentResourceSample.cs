using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class AgentResourceSample
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Guid AgentId { get; set; }

    public DateTime SampledAt { get; set; }

    public double CpuPercent { get; set; }

    public long WorkingSetBytes { get; set; }

    public virtual Agent Agent { get; set; } = null!;

    public virtual Run Run { get; set; } = null!;
}
