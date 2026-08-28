using System.IO.Abstractions;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Core;
using EtlPipelines.Csv;
using EtlPipelines.Files;
using EtlPipelines.Sql;
using EtlPipelines.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.ArchiveToDatabase;

public sealed record Item(int Id, string Name);

/// <summary>
/// A vendor feed as it actually arrives: several CSV files bundled into one zip.
/// </summary>
/// <remarks>
/// Two pipelines, deliberately, not one with an early stage that happens to build a zip. A job like
/// <see cref="Name"/> never creates its own input in real life - something upstream does, on a
/// different schedule and often a different machine - so folding that into the same pipeline would
/// make the sample easier to run but wrong to read as a model of the real thing. Registering the
/// setup as its own pipeline, <see cref="SeedName"/>, means the two are separately nameable on the
/// command line: <c>run build-feed</c> produces the zip, <c>run archive</c> consumes it, and each can
/// be run, listed and reasoned about without the other.
/// </remarks>
public static class Pipeline
{
    /// <summary>
    /// The setup pipeline's name - stands in for whatever vendor process produces the feed this job
    /// consumes. Not part of the job itself; run once, ahead of it, the way a real feed shows up
    /// ahead of the job that reads it.
    /// </summary>
    public const string SeedName = "build-feed";

    /// <summary>The name the real job is registered under - the one this sample is actually about.</summary>
    public const string Name = "archive";

    /// <summary>The database the tables are loaded into.</summary>
    public const string Connection = "archive";

    /// <summary>Where the setup pipeline writes the CSV files before bundling them. Not read by the real job.</summary>
    public const string SourceDirectory = "source";

    /// <summary>Where the real job extracts the archive back out to.</summary>
    public const string ExtractedDirectory = "extracted";

    /// <summary>The feed: built by the setup pipeline, read by the real one.</summary>
    public const string ArchiveFile = "feed.zip";

    /// <summary>One CSV file, and the table it loads into - both named the same.</summary>
    public static readonly string[] Tables = ["customers", "products", "warehouses", "regions", "suppliers"];

    /// <summary>The connection string for the sample's own database, inside its workspace.</summary>
    public static string ConnectionString(IDirectoryInfo directory)
    {
        return $"Data Source={directory.File("archive.db").FullName}";
    }

    /// <summary>
    /// Registers both pipelines against the directory the run is working in.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="workspace">The directory the run reads and writes in.</param>
    /// <param name="configureConnection">
    /// Registers the connection under the name given. Left unset, a SQLite file in the workspace is
    /// used - which is the one thing that differs between loading into SQLite and loading into
    /// another engine.
    /// </param>
    public static IServiceCollection AddPipeline(
        this IServiceCollection services,
        IDirectoryInfo workspace,
        Action<IServiceCollection, string>? configureConnection = null)
    {
        var source = workspace.SubDirectory(SourceDirectory);
        var extracted = workspace.SubDirectory(ExtractedDirectory);
        var archive = workspace.File(ArchiveFile);

        // The vendor's side. Nothing here is the real job - CompressFiles is standing in for whatever
        // that vendor actually uses to bundle its export, no different from calling zip on a shell
        // somewhere else entirely. Registered as its own pipeline so it is never mistaken for a stage
        // of the job that follows it.
        services.AddEtlPipeline(SeedName, builder => builder
            .AddStage<SeedStage>()
            .CompressFiles(source, "*.csv", archive));

        if (configureConnection is not null)
        {
            configureConnection(services, Connection);
        }
        else
        {
            services.AddSqliteConnection(Connection, ConnectionString(workspace));
        }

        var ownsDatabase = configureConnection is null;

        // The real job. It knows nothing about how feed.zip came to exist - only that it should, by
        // the time this runs - which is exactly the assumption a job receiving a vendor's export
        // actually gets to make.
        return services.AddEtlPipeline(Name, builder =>
        {
            builder.WithOptions(options => options.BatchSize = 100)
                .ExtractArchive(archive, extracted);

            if (ownsDatabase)
            {
                builder.AddStage<CreateTablesStage>();
            }

            // One file, one table, five times over - and the five have nothing to do with each other:
            // different tables, different source files, so there is no reason to make the fifth wait
            // on the first four. Each branch still runs its own Truncate/From/To in order - the
            // extraction above has always finished before any of them opens a file - only the five
            // branches themselves overlap. CreateTablesStage already left every table empty when this
            // job owns the database, so TruncateTable has nothing to do there - it earns its place
            // when the database is not this job's own, where "empty" cannot be assumed.
            builder.Parallel(Tables.Select(LoadTable).ToArray());

            Action<IPipelineBuilder> LoadTable(string table)
            {
                return b => b
                    .TruncateTable(Connection, table)
                    .FromCsv<Item>(extracted.File($"{table}.csv"))
                    .ToSqlTable(Connection, table);
            }
        });
    }
}
