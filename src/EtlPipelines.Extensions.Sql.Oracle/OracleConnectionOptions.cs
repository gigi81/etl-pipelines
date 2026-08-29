using EtlPipelines.Extensions.Sql.Connections;

namespace EtlPipelines.Extensions.Sql.Oracle;

/// <summary>Settings applied to every Oracle connection a named registration opens.</summary>
public sealed class OracleConnectionOptions : DbConnectionOptions
{
/// <summary>
/// Sets the schema unqualified names resolve against, for the life of each connection.
/// </summary>
/// <remarks>
/// <para>
/// In Oracle a schema <i>is</i> a user, so a job connecting as <c>ETL</c> finds only <c>ETL</c>'s own
/// tables unless it says otherwise. This issues <c>ALTER SESSION SET CURRENT_SCHEMA</c>, which is how
/// Oracle is asked, and reaches every unqualified name in the run - including the SQL you wrote for
/// <c>FromSql</c>, <c>RunSql</c> and the script stages, which nothing else could qualify for you.
/// </para>
/// <para>
/// <b>It changes name resolution, not privileges.</b> The session still runs as the connecting user,
/// who still needs grants on the other schema's objects; <c>USER</c> goes on reporting the user that
/// connected, and definer's-rights PL/SQL is unaffected. Read as "look here first", not as "become
/// this user".
/// </para>
/// </remarks>
    public string? CurrentSchema { get; set; }
}
