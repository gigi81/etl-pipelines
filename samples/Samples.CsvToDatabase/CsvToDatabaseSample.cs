using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Samples.Common;
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
/// Server, PostgreSQL, MySQL and Oracle and run this very sample against each.
/// </remarks>
public sealed class CsvToDatabaseSample : Sample
{
    /// <summary>The rows this sample loads, and the count the tests expect to arrive.</summary>
    public const int ExpectedRows = 9_500;

    public const string TableName = "trades";

    /// <summary>The database the file is loaded into.</summary>
    public const string Connection = "trades";

    /// <summary>
    /// Registers the connection this sample loads into, under the name it is given. Left unset, the
    /// sample registers a SQLite file of its own.
    /// </summary>
    /// <remarks>
    /// One property where there used to be three. Each provider's <c>Add…Connection</c> brings that
    /// engine's driver and its bulk-load fast path together, so naming the engine is now the whole of
    /// the difference between loading into SQLite and loading into Oracle.
    /// </remarks>
    public Action<IServiceCollection, string>? ConfigureConnection { get; init; }

    /// <summary>The bind marker this engine wants — Oracle uses a colon where most use an at sign.</summary>
    /// <remarks>Only reached when the bulk-load path is turned off; the loaders bind their own.</remarks>
    public string? ParameterPrefix { get; init; }

    public override string PipelineName => "trades";

    public override string Description => "A CSV file loaded into a database table, in one transaction";

    public override void Register(IServiceCollection services, SampleWorkspace workspace)
    {
        if (ConfigureConnection is not null)
        {
            ConfigureConnection(services, Connection);
        }
        else
        {
            services.AddSqliteConnection(Connection, ConnectionString(workspace));
        }

        services.AddEtlPipeline(PipelineName, builder => builder
            .WithOptions(options => options.BatchSize = 1_000)
            .FromCsv<Trade>(workspace.File("trades.csv"))
            .Where(trade => trade.Quantity > 0)
            .ToSqlTable(Connection, TableName, options =>
            {
                if (ParameterPrefix is not null)
                {
                    options.ParameterPrefix = ParameterPrefix;
                }
            }));
    }

    public async override Task PrepareAsync(SampleWorkspace workspace, CancellationToken cancellationToken)
    {
        await SampleData.WriteTradesAsync(workspace.File("trades.csv"), cancellationToken);

        // Only when the sample brought its own database. A caller that registered one has already
        // created the table, in whatever dialect that engine wants.
        if (ConfigureConnection is null)
        {
            await SampleData.CreateSqliteTableAsync(ConnectionString(workspace), cancellationToken);
        }
    }

    private static string ConnectionString(SampleWorkspace workspace) =>
        $"Data Source={workspace.File("trades.db").FullName}";
}
