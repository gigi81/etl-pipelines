using EtlPipelines.Extensions.Sql.Connections;

namespace EtlPipelines.Extensions.Sql.MySql;

/// <summary>Settings applied to every MySql connection a named registration opens.</summary>
public sealed class MySqlConnectionOptions : DbConnectionOptions
{
/// <summary>
/// Sets the database unqualified names resolve against, for the life of each connection.
/// </summary>
/// <remarks>
/// In MySQL and MariaDB a schema <i>is</i> a database, so this issues <c>USE</c>. It reaches every
/// unqualified name in the run - including the SQL you wrote for <c>FromSql</c>, <c>RunSql</c> and
/// the script stages - and changes nothing about what the connecting user is allowed to do there.
/// <para>
/// MySqlConnector restores the original database when a connection returns to the pool, which is
/// exactly why this is applied on every open rather than once.
/// </para>
/// </remarks>
    public string? CurrentSchema { get; set; }
}
