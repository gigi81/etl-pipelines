using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class Run
{
    public Guid Id { get; set; }

    public Guid PipelineId { get; set; }

    public Guid? AgentId { get; set; }

    public string Status { get; set; } = null!;

    public DateTime RequestedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? ExitCode { get; set; }

    public long? RowsRead { get; set; }

    public long? RowsWritten { get; set; }

    public long? RowsFailed { get; set; }

    public virtual Agent? Agent { get; set; }

    public virtual ICollection<AgentResourceSample> AgentResourceSamples { get; set; } = new List<AgentResourceSample>();

    public virtual Pipeline Pipeline { get; set; } = null!;

    public virtual ICollection<StageResult> StageResults { get; set; } = new List<StageResult>();
}
