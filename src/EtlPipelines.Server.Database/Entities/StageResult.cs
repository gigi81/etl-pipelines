using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class StageResult
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public int Sequence { get; set; }

    public string Name { get; set; } = null!;

    public long RowsIn { get; set; }

    public long RowsOut { get; set; }

    public long RowsFailed { get; set; }

    public long ElapsedMs { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorDescription { get; set; }

    public virtual Run Run { get; set; } = null!;
}
