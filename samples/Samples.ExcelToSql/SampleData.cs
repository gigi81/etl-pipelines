using System.Globalization;
using System.IO.Abstractions;
using EtlPipelines.Excel;
using Microsoft.Data.Sqlite;

namespace EtlPipelines.Samples.ExcelToSql;

/// <summary>
/// The workbook somebody filled in and the table it is loaded into.
/// </summary>
/// <remarks>Kept out of the sample itself, which is about the pipeline and not about the fixture.</remarks>
internal static class SampleData
{
    public const int Rows = 200;

    /// <summary>A row as somebody typed it: the amount is text, and not all of it is a number.</summary>
    private sealed class TypedInByHand
    {
        public int Id { get; set; }
        public string Customer { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
    }

    /// <summary>A workbook with a few rows nobody could load, because that is what hand-filled files are like.</summary>
    public static async Task WriteWorkbookAsync(IFileInfo file, CancellationToken cancellationToken)
    {
        var rows = Enumerable.Range(1, Rows)
            .Select(i => new TypedInByHand
            {
                Id = i,
                Customer = $"customer-{i}",
                // Every fortieth row holds something that is not a number at all.
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
    }

    public static async Task CreateTableAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "DROP TABLE IF EXISTS orders; CREATE TABLE orders (Id INTEGER, Customer TEXT, Amount NUMERIC)";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<long> CountAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM orders";

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }
}
