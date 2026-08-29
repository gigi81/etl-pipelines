using Microsoft.Extensions.DependencyInjection;
using System.Data;
using System.Data.Common;
using System.IO.Abstractions;
using System.Reflection;
using EtlPipelines.Extensions.Sql;

// ReSharper disable once CheckNamespace
namespace EtlPipelines;

/// <summary>Shorthand for putting a database table at either end of a dataflow.</summary>
/// <remarks>
/// Two ways to say which database. Naming a connection registered with one of the provider packages'
/// <c>Add…Connection</c> methods is the ordinary one: the connection string comes from configuration,
/// the container owns the factory, and a pipeline can name a different database at each end. Passing
/// an <c>openConnection</c> delegate stays available for a database whose address is only known at
/// run time.
/// </remarks>
public static class ExtensionsSql
{
    /// <summary>Begins a dataflow reading the results of a query, over a named connection.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="connectionName">
    /// The name the connection was registered under, and the name of its connection string under
    /// <c>ConnectionStrings</c>.
    /// </param>
    /// <param name="sql">The query to run.</param>
    /// <param name="map">
    /// Copies the columns of the current row out into a row object. Left <see langword="null"/>,
    /// Dapper's compiled parser for <typeparamref name="TRow"/> is used, matching columns to
    /// properties by name.
    /// </param>
    /// <param name="options">Command settings and parameter binding.</param>
    public static IDataflowBuilder<TRow> FromSql<TRow>(
        this IPipelineBuilder builder,
        string connectionName,
        string sql,
        Func<IDataReader, TRow>? map = null,
        SqlSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

        return builder.From(services => new SqlSource<TRow>(
            services.GetRequiredDbConnectionFactory(connectionName),
            sql,
            map,
            options));
    }

    /// <summary>Begins a dataflow reading the results of a query.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="openConnection">Opens the connection. Called once per run.</param>
    /// <param name="sql">The query to run.</param>
    /// <param name="map">
    /// Copies the columns of the current row out into a row object. Left <see langword="null"/>,
    /// Dapper's compiled parser for <typeparamref name="TRow"/> is used.
    /// </param>
    /// <param name="options">Command settings and parameter binding.</param>
    /// <remarks>
    /// Registered through the factory overload rather than as an instance, so each run builds its own
    /// source. A shared instance would carry an open connection and an exhausted cursor from one run
    /// into the next.
    /// </remarks>
    public static IDataflowBuilder<TRow> FromSql<TRow>(
        this IPipelineBuilder builder,
        Func<CancellationToken, ValueTask<DbConnection>> openConnection,
        string sql,
        Func<IDataReader, TRow>? map = null,
        SqlSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.From(_ => new SqlSource<TRow>(openConnection, sql, map, options));
    }

    /// <summary>Terminates a dataflow by writing into a table, over a named connection.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="connectionName">
    /// The name the connection was registered under, and the name of its connection string under
    /// <c>ConnectionStrings</c>.
    /// </param>
    /// <param name="table">The destination table.</param>
    /// <param name="configure">Adjusts the columns, transaction and bulk-load behaviour.</param>
    /// <remarks>
    /// The bulk loader is taken from the container under the same name as the connection, so a
    /// pipeline writing to two engines gets each one's own fast path. A connection registered without
    /// one — SQLite has no bulk API — falls back to parameterised INSERTs.
    /// </remarks>
    public static IPipelineBuilder ToSqlTable<TRow>(
        this IDataflowBuilder<TRow> builder,
        string connectionName,
        string table,
        Action<SqlSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var options = new SqlSinkOptions { Table = table };
        configure?.Invoke(options);

        return builder.To(services => new SqlSink<TRow>(
            services.GetRequiredDbConnectionFactory(connectionName),
            options,
            services.GetKeyedService<IBulkLoader>(connectionName)));
    }

    /// <summary>Terminates a dataflow by writing into a table.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="openConnection">Opens the connection. Called once per run.</param>
    /// <param name="table">The destination table.</param>
    /// <param name="configure">Adjusts the columns, transaction and bulk-load behaviour.</param>
    /// <remarks>
    /// With no connection name to key on, the bulk loader is whichever unkeyed
    /// <see cref="IBulkLoader"/> the container holds, and the sink falls back to parameterised
    /// INSERTs when there is none. Prefer the named overload when a pipeline writes to more than one
    /// engine, since only one unkeyed loader can be registered.
    /// </remarks>
    public static IPipelineBuilder ToSqlTable<TRow>(
        this IDataflowBuilder<TRow> builder,
        Func<CancellationToken, ValueTask<DbConnection>> openConnection,
        string table,
        Action<SqlSinkOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var options = new SqlSinkOptions { Table = table };
        configure?.Invoke(options);

        return builder.To(services => new SqlSink<TRow>(
            openConnection,
            options,
            services.GetService<IBulkLoader>()));
    }

    /// <summary>Adds a stage that runs a stored procedure.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="procedure">The procedure to call.</param>
    /// <param name="configure">Command timeout, parameters, and what the stage is called.</param>
    /// <remarks>
    /// A stage rather than part of a dataflow, because it moves no rows through this process: it
    /// tells the server to do something with rows an earlier stage already put there. Reach for it
    /// when the work is far cheaper next to the data than round-tripped out here and back.
    /// </remarks>
    public static IPipelineBuilder RunStoredProcedure(
        this IPipelineBuilder builder,
        string connectionName,
        string procedure,
        Action<SqlCommandOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SqlCommandOptions();
        configure?.Invoke(options);

        return builder.AddStage(
            new SqlCommandStage(connectionName, procedure, CommandType.StoredProcedure, options));
    }

    /// <summary>Adds a stage that runs one SQL statement.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="sql">The statement to run.</param>
    /// <param name="configure">Command timeout, parameters, and what the stage is called.</param>
    /// <remarks>
    /// <para>
    /// For the one-liner that does not warrant a file: truncating a staging table, setting a flag,
    /// calling something that is not a procedure. Bind values through
    /// <see cref="SqlCommandOptions.Configure"/> rather than building the string, which is how a
    /// value containing a quote becomes an injection.
    /// </para>
    /// <para>
    /// One command, sent as given: nothing is split on <c>GO</c> or on a changed delimiter. Several
    /// statements, or anything with a procedure body in it, belong in a file and go through
    /// <see cref="RunSqlScript(IPipelineBuilder, string, IFileInfo, Action{SqlScriptOptions})"/>, which splits them the way the engine needs.
    /// </para>
    /// </remarks>
    public static IPipelineBuilder RunSql(
        this IPipelineBuilder builder,
        string connectionName,
        string sql,
        Action<SqlCommandOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SqlCommandOptions();
        configure?.Invoke(options);

        return builder.AddStage(new SqlCommandStage(connectionName, sql, CommandType.Text, options));
    }

    /// <summary>Adds a stage that empties a table.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="table">The table to empty.</param>
    /// <param name="configure">Command timeout and what the stage is called.</param>
    /// <remarks>
    /// Runs whatever the connection's own engine actually supports, resolved from the
    /// <see cref="ITruncateStatement"/> registered under its name — real <c>TRUNCATE TABLE</c> for
    /// SQL Server, PostgreSQL, MySQL and Oracle, and <c>DELETE FROM</c> for SQLite, which has no
    /// <c>TRUNCATE</c> statement at all. Each provider package registers the right one, so naming a
    /// connection is all this needs; nothing here asks the caller to know which engine is on the other
    /// end.
    /// </remarks>
    public static IPipelineBuilder TruncateTable(
        this IPipelineBuilder builder,
        string connectionName,
        string table,
        Action<SqlCommandOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SqlCommandOptions();
        configure?.Invoke(options);

        return builder.AddStage(new TruncateTableStage(connectionName, table, options));
    }

    /// <summary>Adds a stage that runs a SQL script file.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="script">The file to run.</param>
    /// <param name="configure">Command timeout and what the stage is called.</param>
    /// <remarks>
    /// The file is split into batches by the <see cref="ISqlScriptParser"/> registered under the same
    /// connection name — <c>GO</c> for SQL Server, <c>DELIMITER</c> for MySQL, terminator detection
    /// for Oracle, and the whole file at once for PostgreSQL and SQLite. Use
    /// <see cref="RunEmbeddedSqlScript"/> for a script compiled into an assembly instead.
    /// </remarks>
    public static IPipelineBuilder RunSqlScript(
        this IPipelineBuilder builder,
        string connectionName,
        IFileInfo script,
        Action<SqlScriptOptions>? configure = null) =>
        builder.RunSqlScript(connectionName, new FileSqlScriptSource(script), configure);

    /// <summary>Adds a stage that runs a SQL script compiled into an assembly.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="assembly">The assembly the script is embedded in.</param>
    /// <param name="resourceName">
    /// The resource's logical name, or the tail of it: <c>create-staging.sql</c> finds
    /// <c>My.App.Scripts.create-staging.sql</c> as long as nothing else ends the same way.
    /// </param>
    /// <param name="configure">Command timeout and what the stage is called.</param>
    /// <remarks>
    /// A script that ships inside the application rather than beside it: nothing to copy on deploy
    /// and nothing to go missing between the build and the run. The file has to be marked as an
    /// <c>EmbeddedResource</c> in its project; when the name does not match, the error lists what the
    /// assembly actually holds.
    /// </remarks>
    public static IPipelineBuilder RunEmbeddedSqlScript(
        this IPipelineBuilder builder,
        string connectionName,
        Assembly assembly,
        string resourceName,
        Action<SqlScriptOptions>? configure = null) =>
        builder.RunSqlScript(connectionName, new EmbeddedSqlScriptSource(assembly, resourceName), configure);

    /// <summary>Adds a stage that runs a SQL script from wherever <paramref name="script"/> reads it.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="connectionName">The name the connection was registered under.</param>
    /// <param name="script">Where the script is read from.</param>
    /// <param name="configure">Command timeout and what the stage is called.</param>
    /// <remarks>
    /// The general form the other two are built on, for a script that comes from somewhere neither
    /// covers — an object store, a configuration service.
    /// </remarks>
    public static IPipelineBuilder RunSqlScript(
        this IPipelineBuilder builder,
        string connectionName,
        ISqlScriptSource script,
        Action<SqlScriptOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new SqlScriptOptions();
        configure?.Invoke(options);

        return builder.AddStage(new SqlScriptStage(connectionName, script, options));
    }
}
