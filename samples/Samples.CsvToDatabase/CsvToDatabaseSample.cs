using System.Data.Common;
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
/// The pipeline is the same for every engine: only the connection and the bulk loader change, which
/// is why they are the two things this sample takes as properties. Left alone it uses a SQLite file
/// in its own workspace, and the integration tests set them to run this very sample against SQL
/// Server, PostgreSQL, MySQL and Oracle.
/// </remarks>
public sealed class CsvToDatabaseSample : Sample
{
    /// <summary>The rows this sample loads, and the count the tests expect to arrive.</summary>
    public const int ExpectedRows = 9_500;

    public const string TableName = "trades";

    /// <summary>How to open a connection. Left unset, the sample uses a SQLite file of its own.</summary>
    public Func<CancellationToken, ValueTask<DbConnection>>? OpenConnection { get; init; }

    /// <summary>
    /// The provider's fast path, when the caller has one. Without it the sink falls back to
    /// parameterised INSERTs, which behave the same everywhere but are slower.
    /// </summary>
    public IBulkLoader? BulkLoader { get; init; }

    /// <summary>The bind marker this engine wants — Oracle uses a colon where most use an at sign.</summary>
    public string? ParameterPrefix { get; init; }

    public override string PipelineName => "trades";

    public override string Description => "A CSV file loaded into a database table, in one transaction";

    public override void Register(IServiceCollection services, SampleWorkspace workspace)
    {
        if (BulkLoader is not null)
        {
            services.AddSingleton(BulkLoader);
        }

        services.AddEtlPipeline(PipelineName, builder => builder
            .WithOptions(options => options.BatchSize = 1_000)
            .FromCsv<Trade>(workspace.File("trades.csv"))
            .Where(trade => trade.Quantity > 0)
            .ToSqlTable(Open(workspace), TableName, options =>
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

        // Only when the sample brought its own database. A caller that handed one over has already
        // created the table, in whatever dialect that engine wants.
        if (OpenConnection is null)
        {
            await SampleData.CreateSqliteTableAsync(ConnectionString(workspace), cancellationToken);
        }
    }

    private Func<CancellationToken, ValueTask<DbConnection>> Open(SampleWorkspace workspace) =>
        OpenConnection ?? SqliteConnections.Open(ConnectionString(workspace));

    private static string ConnectionString(SampleWorkspace workspace) =>
        $"Data Source={workspace.File("trades.db").FullName}";
}
