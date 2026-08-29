using EtlPipelines.Extensions.Sql.Connections;

namespace EtlPipelines.Extensions.Sql.PostgreSql;

/// <summary>Settings applied to every PostgreSql connection a named registration opens.</summary>
public sealed class PostgreSqlConnectionOptions : DbConnectionOptions
{
/// <summary>
/// Sets the schema unqualified names resolve against, for the life of each connection.
/// </summary>
/// <remarks>
/// Issues <c>SET search_path</c>, and reaches every unqualified name in the run - including the SQL
/// you wrote for <c>FromSql</c>, <c>RunSql</c> and the script stages. It changes where names are
/// looked up and nothing else: the connecting role still needs its privileges on that schema.
/// <para>
/// Npgsql discards session state when a connection returns to the pool, which is exactly why this is
/// applied on every open rather than once.
/// </para>
/// </remarks>
    public string? CurrentSchema { get; set; }
}
