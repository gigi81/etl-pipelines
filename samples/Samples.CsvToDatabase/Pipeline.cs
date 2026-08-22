using System.IO.Abstractions;
using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.CsvToDatabase;

public sealed record Trade(int Id, string Symbol, decimal Price, int Quantity);

/// <summary>
/// Loading a file into a table, against whichever database is in front of you.
/// </summary>
/// <remarks>
/// The pipeline is the same for every engine, and it names its database rather than holding one — so
/// the only thing that changes between engines is which <c>Add…Connection</c> registered that name.
/// Left alone it registers a SQLite file in its own workspace; the integration tests substitute SQL
/// Server, PostgreSQL, MySQL and Oracle and run this very registration against each.
/// </remarks>
public static class TradesPipeline
{
    /// <summary>The name the pipeline is registered under.</summary>
    public const string Name = "trades";

    /// <summary>The database the file is loaded into.</summary>
    public const string Connection = "trades";

    /// <summary>The table rows land in.</summary>
    public const string Table = "trades";

    /// <summary>The incoming file, as the sample's own seeder writes it.</summary>
    public const string InputFile = "trades.csv";

    /// <summary>The rows that survive the filter, and the count the tests expect to arrive.</summary>
    public const int ExpectedRows = 9_500;

    /// <summary>The connection string for the sample's own database, inside its workspace.</summary>
    public static string ConnectionString(IDirectoryInfo directory)
    {
        return $"Data Source={directory.File("trades.db").FullName}";
    }

    /// <summary>Registers the pipeline against the directory the run is working in.</summary>
    /// <param name="services">The container.</param>
    /// <param name="directory">The directory the run reads and writes in.</param>
    /// <param name="configureConnection">
    /// Registers the connection under the name given. Left unset, a SQLite file in the workspace is
    /// used — which is the one thing that differs between loading into SQLite and loading into Oracle.
    /// </param>
    /// <param name="parameterPrefix">
    /// The bind marker this engine wants — Oracle uses a colon where most use an at sign. Only
    /// reached when the bulk-load path is turned off; the loaders bind their own.
    /// </param>
    public static IServiceCollection AddTradesPipeline(
        this IServiceCollection services,
        IDirectoryInfo directory,
        Action<IServiceCollection, string>? configureConnection = null,
        string? parameterPrefix = null)
    {
        if (configureConnection is not null)
        {
            configureConnection(services, Connection);
        }
        else
        {
            services.AddSqliteConnection(Connection, ConnectionString(directory));
        }

        var ownsDatabase = configureConnection is null;

        return services.AddEtlPipeline(Name, builder =>
        {
            builder.WithOptions(options => options.BatchSize = 1_000)
                // The file this job loads does not exist until something fetches it.
                .AddStage<TradesData>();

            if (ownsDatabase)
            {
                builder.AddStage<TradesTable>();
            }

            builder
            .FromCsv<Trade>(directory.File(InputFile))
            .Where(trade => trade.Quantity > 0)
            .ToSqlTable(Connection, Table, options =>
            {
                if (parameterPrefix is not null)
                {
                    options.ParameterPrefix = parameterPrefix;
                }
            });
        });
    }
}
