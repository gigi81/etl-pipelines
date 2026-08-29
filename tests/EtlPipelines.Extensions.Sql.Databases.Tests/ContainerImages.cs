namespace EtlPipelines.Extensions.Sql.Databases.Tests;

/// <summary>
/// The container images the database tests run against.
/// </summary>
/// <remarks>
/// These are the images Testcontainers used to pick on its own, before it deprecated doing so. A
/// default that moves with the package version means a test run can change what it is testing
/// without anything in this repository changing, so they are pinned here instead: upgrading one is
/// then a visible edit, in one place, rather than a side effect of a dependency bump.
/// </remarks>
public static class ContainerImages
{
    public const string SqlServer = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    public const string PostgreSql = "postgres:15.1";

    public const string MySql = "mysql:8.0";

    /// <summary>
    /// MariaDB is served by the MySQL connector rather than one of its own, which is exactly why it
    /// is worth a container: the claim that one bulk loader covers both is otherwise untested.
    /// </summary>
    public const string MariaDb = "mariadb:11.4";

    public const string Oracle = "gvenzl/oracle-xe:21.3.0-slim-faststart";
}
