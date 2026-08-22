using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oracle.ManagedDataAccess.Client;

namespace EtlPipelines.Sql.Oracle;

/// <summary>Loads batches through ODP.NET array binding.</summary>
/// <remarks>
/// <para>
/// Array binding sends one INSERT with a column-shaped array behind each bind variable, so a batch
/// costs a single round trip instead of one per row. It is used here in preference to
/// <c>OracleBulkCopy</c> because it takes part in the transaction the sink already opened, which is
/// what keeps a failed run from leaving rows behind; OracleBulkCopy manages its own.
/// </para>
/// <para>
/// The batch has to be turned from rows into columns first, which is the one place this loader
/// materialises anything — bounded by the pipeline's batch size, not by the size of the load.
/// </para>
/// </remarks>
public sealed class OracleBulkLoader : IBulkLoader
{
    /// <inheritdoc />
    public async ValueTask<int> LoadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns,
        DbDataReader rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);

        var values = new List<object?>[columns.Count];
        var types = new Type[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            values[i] = [];

            // Captured from the schema before the rows are consumed: ODP.NET will not infer a bind
            // type from an object[], so each parameter has to be told what it is carrying.
            types[i] = rows.GetFieldType(i);
        }

        var count = 0;
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < columns.Count; i++)
            {
                values[i].Add(rows.GetValue(i));
            }

            count++;
        }

        if (count == 0)
        {
            return 0;
        }

        await using var command = (OracleCommand)connection.CreateCommand();
        command.Transaction = (OracleTransaction?)transaction;

        // Oracle binds with a colon, not an at sign.
        var columnList = string.Join(", ", columns);
        var binds = string.Join(", ", columns.Select((_, i) => $":p{i}"));
        command.CommandText = $"INSERT INTO {table} ({columnList}) VALUES ({binds})";

        // The whole point: one execution carrying every row in the batch.
        command.ArrayBindCount = count;
        command.BindByName = true;

        for (var i = 0; i < columns.Count; i++)
        {
            command.Parameters.Add(new OracleParameter
            {
                ParameterName = $"p{i}",
                OracleDbType = BindTypeFor(types[i]),
                Value = values[i].ToArray(),
            });
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>The Oracle bind type for a column's CLR type.</summary>
    /// <remarks>
    /// Array binding hands ODP.NET an <c>object[]</c> per column, which it will not infer a type from
    /// — it fails with "Value does not fall within the expected range" rather than saying so. Setting
    /// the type explicitly is what makes the bind work.
    /// </remarks>
    private static OracleDbType BindTypeFor(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 => OracleDbType.Int32,
        TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => OracleDbType.Int64,
        TypeCode.Single => OracleDbType.BinaryFloat,
        TypeCode.Double => OracleDbType.BinaryDouble,
        TypeCode.Decimal => OracleDbType.Decimal,
        TypeCode.DateTime => OracleDbType.Date,
        TypeCode.String or TypeCode.Char => OracleDbType.Varchar2,
        _ when type == typeof(byte[]) => OracleDbType.Blob,
        _ when type == typeof(Guid) => OracleDbType.Raw,
        _ when type == typeof(DateTimeOffset) => OracleDbType.TimeStampTZ,
        _ when type == typeof(TimeSpan) => OracleDbType.IntervalDS,
        _ => OracleDbType.Varchar2,
    };
}

/// <summary>Registers the Oracle bulk-load path.</summary>
public static class OracleExtensions
{
    /// <summary>Makes <see cref="SqlSink{TRow}"/> use array binding instead of one INSERT per row.</summary>
    public static IServiceCollection AddOracleBulkLoader(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IBulkLoader, OracleBulkLoader>();
        return services;
    }

    /// <summary>A factory that opens a new connection per run.</summary>
    public static Func<CancellationToken, ValueTask<DbConnection>> Open(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return async cancellationToken =>
        {
            var connection = new OracleConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        };
    }

    /// <summary>
    /// Registers a named Oracle connection, taking its connection string from configuration, and
    /// that engine's bulk-load fast path alongside it.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="name">
    /// The name <c>FromSql</c> and <c>ToSqlTable</c> refer to, and the name of the connection string
    /// under <c>ConnectionStrings</c>.
    /// </param>
    /// <remarks>
    /// The loader and the script parser are keyed to the connection name rather than registered once for
    /// the container, so a pipeline that touches two engines gets the right one at each end.
    /// </remarks>
    public static IServiceCollection AddOracleConnection(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, OpenAsync);
        services.AddKeyedSingleton<IBulkLoader, OracleBulkLoader>(name);
        services.AddKeyedSingleton<ISqlScriptParser, OracleScriptParser>(name);

        return services;
    }

    /// <summary>
    /// Registers a named Oracle connection with the connection string given here rather than read
    /// from configuration, and that engine's bulk-load fast path alongside it.
    /// </summary>
    /// <remarks>
    /// For a database whose address is only known at run time — a throwaway container in a test.
    /// </remarks>
    public static IServiceCollection AddOracleConnection(
        this IServiceCollection services,
        string name,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbConnection(name, connectionString, OpenAsync);
        services.AddKeyedSingleton<IBulkLoader, OracleBulkLoader>(name);
        services.AddKeyedSingleton<ISqlScriptParser, OracleScriptParser>(name);

        return services;
    }

    private static async ValueTask<DbConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
