using System.IO.Abstractions;
using EtlPipelines.Samples.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.CsvToDatabase;

/// <summary>
/// The file this sample loads, and the SQLite table it loads into when nobody supplies one.
/// </summary>
public sealed class TradesData
{
    private readonly SampleWorkspace _workspace;
    private readonly ILogger<TradesData> _logger;

    public TradesData(SampleWorkspace workspace, ILogger<TradesData> logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(logger);

        _workspace = workspace;
        _logger = logger;
    }

    /// <summary>Rows written, of which every twentieth is a cancellation the pipeline filters out.</summary>
    public const int Rows = 10_000;

    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        var file = _workspace.File(TradesPipeline.InputFile);
        var lines = new List<string>(Rows + 1) { "Id,Symbol,Price,Quantity" };
        var symbols = new[] { "ACME", "GLBX", "INIT", "UMBR" };

        for (var i = 1; i <= Rows; i++)
        {
            var quantity = i % 20 == 0 ? 0 : (i % 500) + 1;
            lines.Add($"{i},{symbols[i % symbols.Length]},{(i % 1000) + 0.25m},{quantity}");
        }

        await file.WriteAllLinesAsync(lines, cancellationToken);
        _logger.LogInformation("Wrote {Rows} trades to {File}", Rows, file.FullName);
    }

    /// <summary>Creates the table in the sample's own SQLite database.</summary>
    /// <remarks>
    /// Only for the SQLite path. A caller that registered another engine has already created the
    /// table, in whatever dialect that engine wants.
    /// </remarks>
    public async Task CreateSqliteTableAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(TradesPipeline.ConnectionString(_workspace));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {TradesPipeline.Table}; " +
            $"CREATE TABLE {TradesPipeline.Table} (Id INTEGER, Symbol TEXT, Price NUMERIC, Quantity INTEGER)";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
