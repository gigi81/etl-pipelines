namespace EtlPipelines.Extensions.Sql.Configuration;

/// <summary>Settings shared by the SQL source and sink.</summary>
public abstract class SqlOptions
{
    /// <summary>
    /// How long a command may run before the provider gives up, in seconds. Left <see langword="null"/>
    /// the provider's own default applies.
    /// </summary>
    public int? CommandTimeout { get; set; }
}
