using System.Data.Common;

namespace EtlPipelines.Sql.Configuration;

/// <summary>Settings for running a statement or a stored procedure.</summary>
public sealed class SqlCommandOptions : SqlStageOptions
{
    /// <summary>
    /// Binds parameters to the command before it runs. Use this rather than pasting values into the
    /// SQL, which is how a value that contains a quote becomes an injection.
    /// </summary>
    public Action<DbCommand>? Configure { get; set; }
}
