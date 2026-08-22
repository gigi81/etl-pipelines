using System.IO.Abstractions;
using Microsoft.Data.Sqlite;

namespace EtlPipelines.Samples.CsvToDatabase;

/// <summary>
/// The file this sample loads, and the SQLite table it loads into when nobody supplies one.
/// </summary>
/// <remarks>Kept out of the sample itself, where the pipeline should be the longest thing on the page.</remarks>
internal static class SampleData
{
    public const int Rows = 10_000;

    public static Task WriteTradesAsync(IFileInfo file, CancellationToken cancellationToken)
    {
        var lines = new List<string>(Rows + 1) { "Id,Symbol,Price,Quantity" };
        var symbols = new[] { "ACME", "GLBX", "INIT", "UMBR" };

        for (var i = 1; i <= Rows; i++)
        {
            // Every twentieth row is a cancellation, which the pipeline filters out.
            var quantity = i % 20 == 0 ? 0 : (i % 500) + 1;
            lines.Add($"{i},{symbols[i % symbols.Length]},{(i % 1000) + 0.25m},{quantity}");
        }

        return file.WriteAllLinesAsync(lines, cancellationToken);
    }

    public static async Task CreateSqliteTableAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {CsvToDatabaseSample.TableName}; " +
            $"CREATE TABLE {CsvToDatabaseSample.TableName} " +
            "(Id INTEGER, Symbol TEXT, Price NUMERIC, Quantity INTEGER)";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
