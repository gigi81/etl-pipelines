using ErrorOr;
using System.Diagnostics;
using System.IO.Abstractions;
using System.Globalization;
using EtlPipelines.Excel;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Samples.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.ExcelToSql;

/// <summary>
/// The workbook somebody filled in, and the table it is loaded into.
/// </summary>
public sealed class ImportData : IPipelineStage
{
    private readonly IDirectoryInfo _directory;
    private readonly ILogger<ImportData> _logger;

    public ImportData(
        [FromKeyedServices(SampleWorkspace.Key)] IDirectoryInfo directory,
        ILogger<ImportData> logger)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(logger);

        _directory = directory;
        _logger = logger;
    }

    /// <summary>Rows written, of which every fortieth holds something that is not a number.</summary>
    public const int Rows = 200;

    /// <summary>A row as somebody typed it: the amount is text, and not all of it is a number.</summary>
    private sealed class TypedInByHand
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
    }

    /// <summary>The name this step appears under in the run's report.</summary>
    public string Name => "collect";

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();

        await WriteWorkbookAsync(cancellationToken);
        await CreateTableAsync(cancellationToken);

        // No rows in or out: what this step produced is a file and a table, not rows through the
        // framework, and a stage that reports none is skipped when the run's totals are worked out.
        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    /// <summary>A workbook with a few rows nobody could load, because that is what hand-filled files are like.</summary>
    private async Task WriteWorkbookAsync(CancellationToken cancellationToken)
    {
        var file = _directory.File(ImportPipeline.InputFile);

        var rows = Enumerable.Range(1, Rows)
            .Select(i => new TypedInByHand
            {
                Id = i,
                Customer = $"customer-{i}",
                Amount = i % 40 == 0
                    ? "not filled in"
                    : (i * 1.25m).ToString(CultureInfo.InvariantCulture),
            })
            .ToArray();

        var sink = new ExcelSink<TypedInByHand>(file);
        await using (sink)
        {
            await sink.InitializeAsync(cancellationToken);
            await sink.WriteAsync(rows, cancellationToken);
            await sink.CompleteAsync(cancellationToken);
        }

        _logger.LogInformation("Wrote {Rows} rows to {File}", Rows, file.FullName);
    }

    private async Task CreateTableAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ImportPipeline.ConnectionString(_directory));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {ImportPipeline.Table}; " +
            $"CREATE TABLE {ImportPipeline.Table} (Id INTEGER, Customer TEXT, Amount NUMERIC)";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

}
