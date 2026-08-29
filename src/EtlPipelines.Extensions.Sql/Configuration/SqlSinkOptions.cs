namespace EtlPipelines.Sql.Configuration;

/// <summary>Settings for writing to a database table.</summary>
public sealed class SqlSinkOptions : SqlOptions
{
    /// <summary>The table rows are written to. Required.</summary>
    public required string Table { get; set; }

    /// <summary>
    /// The columns to write, defaulting to every readable property of the row type, matched by name.
    /// </summary>
    /// <remarks>
    /// Name one or more columns to write a subset — leaving out an identity column the database fills
    /// in itself, for one. The order given does not matter; the row type's own order is kept.
    /// </remarks>
    public IReadOnlyCollection<string>? Columns { get; set; }

    /// <summary>
    /// Whether the whole run is written inside one transaction, committed only when it succeeds.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// This is the database equivalent of the file connectors' temporary-file-and-rename: a run that
    /// fails part-way leaves the table as it was, rather than holding some fraction of the rows for a
    /// downstream job to read as though the load had finished. Turn it off for a load large enough
    /// that one transaction would strain the server's log, and accept partial writes in exchange.
    /// </remarks>
    public bool UseTransaction { get; set; } = true;

    /// <summary>
    /// The character a bind parameter's name starts with. Defaults to <c>@</c>.
    /// </summary>
    /// <remarks>
    /// Providers do not agree on this, and the disagreement is silent: SQL Server, SQLite, MySQL and
    /// PostgreSQL all take <c>@p0</c>, while Oracle wants <c>:p0</c> and simply will not bind a
    /// parameter it cannot find. Set this to <c>:</c> for Oracle when forcing the INSERT path — the
    /// Oracle package's bulk loader, which is used by default, binds arrays directly and does not go
    /// through this.
    /// </remarks>
    public string ParameterPrefix { get; set; } = "@";

    /// <summary>
    /// Whether to use the provider's bulk-load path when the connector supplies one. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Bulk loaders are far faster but do not all behave identically to an INSERT — some bypass
    /// triggers, some take different locks. Set this to <see langword="false"/> to force the
    /// parameterised INSERT path, which behaves the same everywhere.
    /// </remarks>
    public bool UseBulkLoader { get; set; } = true;
}
