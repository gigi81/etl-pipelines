using ErrorOr;
using System.Diagnostics;
using System.IO.Abstractions;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Samples.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.CsvToDatabase;

/// <summary>
/// The file this sample loads, and the SQLite table it loads into when nobody supplies one.
/// </summary>
public sealed class TradesData : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<TradesData> _logger;

    public TradesData(
        [FromKeyedServices(SampleWorkspace.Key)] IDirectoryInfo directory,
        ILogger<TradesData> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>Rows written, of which every twentieth is a cancellation the pipeline filters out.</summary>
    public const int Rows = 10_000;

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "fetch";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        var file = _directory.File(TradesPipeline.InputFile);
        var lines = new List<string>(Rows + 1) { "Id,Symbol,Price,Quantity" };
        var symbols = new[] { "ACME", "GLBX", "INIT", "UMBR" };

        for (var i = 1; i <= Rows; i++)
        {
            var quantity = i % 20 == 0 ? 0 : (i % 500) + 1;
            lines.Add($"{i},{symbols[i % symbols.Length]},{(i % 1000) + 0.25m},{quantity}");
        }

        await file.WriteAllLinesAsync(lines, cancellationToken);
        _logger.LogInformation("Wrote {Rows} trades to {File}", Rows, file.FullName);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}

/// <summary>
/// Creates the destination table in the sample's own SQLite database.
/// </summary>
/// <remarks>
/// A stage of its own, and one the pipeline only adds when it brought its own database: a caller that
/// registered another engine has already created the table, in whatever dialect that engine wants.
/// Conditionally adding a step is the ordinary way to say that.
/// </remarks>
public sealed class TradesTable : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<TradesTable> _logger;

    public TradesTable(
        [FromKeyedServices(SampleWorkspace.Key)] IDirectoryInfo directory,
        ILogger<TradesTable> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "create-table";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        await using var connection = new SqliteConnection(TradesPipeline.ConnectionString(_directory));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {TradesPipeline.Table}; " +
            $"CREATE TABLE {TradesPipeline.Table} (Id INTEGER, Symbol TEXT, Price NUMERIC, Quantity INTEGER)";

        await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogInformation("Created {Table}", TradesPipeline.Table);

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }
}
