using System;
using System.Collections.Generic;

namespace EtlPipelines.Server.Database.Entities;

public partial class ConfigurationEntry
{
    public Guid Id { get; set; }

    public string Key { get; set; } = null!;

    public byte[] EncryptedValue { get; set; } = null!;

    public DateTime UpdatedAt { get; set; }
}
