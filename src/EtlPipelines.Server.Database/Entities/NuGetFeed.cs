using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class NuGetFeed
{
    public Guid Id { get; set; }

    public string Url { get; set; } = null!;

    public int Ordinal { get; set; }
}
