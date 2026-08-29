using System.Data;

namespace EtlPipelines.Sql.Configuration;

/// <summary>Settings for reading from a database.</summary>
public sealed class SqlSourceOptions : SqlOptions
{
    /// <summary>How the command text should be interpreted. Defaults to <see cref="CommandType.Text"/>.</summary>
    public CommandType CommandType { get; set; } = CommandType.Text;

    /// <summary>
    /// Binds parameters to the command before it runs. Use this rather than pasting values into the
    /// SQL, which is how a value that contains a quote becomes an injection.
    /// </summary>
    public Action<IDbCommand>? Configure { get; set; }
}
