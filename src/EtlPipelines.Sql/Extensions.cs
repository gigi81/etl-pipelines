using System.Data;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Sql;

/// <summary>Shorthand for putting a database table at either end of a dataflow.</summary>
public static class Extensions
{
    /// <summary>Begins a dataflow reading the results of a query.</summary>
    /// <param name="builder">The pipeline being composed.</param>
    /// <param name="openConnection">Opens the connection. Called once per run.</param>
    /// <param name="sql">The query to run.</param>
    /// <param name="map">Copies the columns of the current row out into a row object.</param>
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
        Func<IDataRecord, TRow> map,
        SqlSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.From(_ => new SqlSource<TRow>(openConnection, sql, map, options));
    }

    /// <summary>Terminates a dataflow by writing into a table.</summary>
    /// <param name="builder">The dataflow being composed.</param>
    /// <param name="openConnection">Opens the connection. Called once per run.</param>
    /// <param name="table">The destination table.</param>
    /// <param name="configure">Adjusts the columns, transaction and bulk-load behaviour.</param>
    /// <remarks>
    /// The provider's <see cref="IBulkLoader"/> is taken from the container when one is registered —
    /// referencing <c>EtlPipelines.Sql.SqlServer</c> and calling its <c>AddSqlServerBulkLoader</c> is
    /// all it takes to turn the INSERT path into a SqlBulkCopy — and the sink falls back to
    /// parameterised INSERTs when none is.
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
}
