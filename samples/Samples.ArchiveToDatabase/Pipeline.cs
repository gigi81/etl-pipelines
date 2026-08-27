using System.IO.Abstractions;
using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Files;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.ArchiveToDatabase;

public sealed record Item(int Id, string Name);

/// <summary>
/// A vendor feed as it actually arrives: several CSV files bundled into one zip. The pipeline builds
/// that zip itself so the sample is self-contained, then does what a real job does with one -
/// extracts it and loads each file into its own table.
/// </summary>
/// <remarks>
/// Compress and extract are coarse stages, not ports: they chain on <see cref="IPipelineBuilder"/> the
/// same way <c>RunSql</c> does, not inside a dataflow. Everything downstream of the extraction is the
/// ordinary CSV-to-SQL shape from the <c>CsvToDatabase</c> sample, repeated once per file.
/// </remarks>
public static class Pipeline
{
    /// <summary>The name the pipeline is registered under.</summary>
    public const string Name = "archive";

    /// <summary>The database the tables are loaded into.</summary>
    public const string Connection = "archive";

    /// <summary>Where the sample writes the CSV files before bundling them.</summary>
    public const string SourceDirectory = "source";

    /// <summary>Where the archive is extracted back out to.</summary>
    public const string ExtractedDirectory = "extracted";

    /// <summary>The archive the sample builds and then reads back.</summary>
    public const string ArchiveFile = "feed.zip";

    /// <summary>One CSV file, and the table it loads into - both named the same.</summary>
    public static readonly string[] Tables = ["customers", "products", "warehouses", "regions", "suppliers"];

    /// <summary>The connection string for the sample's own database, inside its workspace.</summary>
    public static string ConnectionString(IDirectoryInfo directory)
    {
        return $"Data Source={directory.File("archive.db").FullName}";
    }

    /// <summary>Registers the pipeline against the directory the run is working in.</summary>
    /// <param name="services">The container.</param>
    /// <param name="directory">The directory the run reads and writes in.</param>
    /// <param name="configureConnection">
    /// Registers the connection under the name given. Left unset, a SQLite file in the workspace is
    /// used - which is the one thing that differs between loading into SQLite and loading into
    /// another engine.
    /// </param>
    public static IServiceCollection AddPipeline(
        this IServiceCollection services,
        IDirectoryInfo directory,
        Action<IServiceCollection, string>? configureConnection = null)
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

        var source = directory.SubDirectory(SourceDirectory);
        var extracted = directory.SubDirectory(ExtractedDirectory);
        var archive = directory.File(ArchiveFile);

        return services.AddEtlPipeline(Name, builder =>
        {
            builder.WithOptions(options => options.BatchSize = 100)
                // The five files this job reads do not exist until something writes them.
                .AddStage<SeedStage>()
                // Bundled into one zip, the way a vendor would actually hand them over...
                .CompressFiles(source, "*.csv", archive)
                // ...and unpacked again, the way a job receiving that zip actually starts.
                .ExtractArchive(archive, extracted);

            if (ownsDatabase)
            {
                builder.AddStage<CreateTablesStage>();
            }

            // One file, one table, five times over - each From/To pair is its own stage, and stages
            // run one after another, so the extraction above has always finished before the first
            // one opens its file.
            builder
                .FromCsv<Item>(extracted.File($"{Tables[0]}.csv")).ToSqlTable(Connection, Tables[0])
                .FromCsv<Item>(extracted.File($"{Tables[1]}.csv")).ToSqlTable(Connection, Tables[1])
                .FromCsv<Item>(extracted.File($"{Tables[2]}.csv")).ToSqlTable(Connection, Tables[2])
                .FromCsv<Item>(extracted.File($"{Tables[3]}.csv")).ToSqlTable(Connection, Tables[3])
                .FromCsv<Item>(extracted.File($"{Tables[4]}.csv")).ToSqlTable(Connection, Tables[4]);
        });
    }
}
