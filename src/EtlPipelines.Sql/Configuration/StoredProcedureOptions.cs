using System.Data.Common;

namespace EtlPipelines.Sql.Configuration;

/// <summary>Settings for running a stored procedure.</summary>
public sealed class StoredProcedureOptions : SqlStageOptions
{
    /// <summary>
    /// Binds parameters to the command before it runs. Use this rather than pasting values into the
    /// procedure name, which is how a value that contains a quote becomes an injection.
    /// </summary>
    public Action<DbCommand>? Configure { get; set; }
}
