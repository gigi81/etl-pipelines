using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class Agent
{
    public Guid Id { get; set; }

    public string MachineName { get; set; } = null!;

    public List<string> Tags { get; set; } = null!;

    public string Version { get; set; } = null!;

    public string Status { get; set; } = null!;

    public DateTime LastHeartbeatAt { get; set; }

    public virtual ICollection<AgentResourceSample> AgentResourceSamples { get; set; } = new List<AgentResourceSample>();

    public virtual ICollection<Run> Runs { get; set; } = new List<Run>();
}
