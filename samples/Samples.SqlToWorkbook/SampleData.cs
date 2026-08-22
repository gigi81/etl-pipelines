using Microsoft.Data.Sqlite;

namespace EtlPipelines.Samples.SqlToWorkbook;

/// <summary>
/// Fills the database this sample reports on.
/// </summary>
/// <remarks>Kept out of the sample itself, where it would bury the three queries that are the point.</remarks>
internal static class SampleData
{
    public const int Rows = 200;

    public static async Task SeedAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP TABLE IF EXISTS orders;
            DROP TABLE IF EXISTS customers;
            DROP TABLE IF EXISTS region_totals;

            CREATE TABLE orders (Id INTEGER PRIMARY KEY, Customer TEXT, Amount NUMERIC);
            CREATE TABLE customers (Name TEXT, Country TEXT);
            CREATE TABLE region_totals (Region TEXT, Total NUMERIC);

            INSERT INTO orders (Customer, Amount)
            SELECT 'customer-' || value, value * 1.5 FROM {Series};

            INSERT INTO customers (Name, Country)
            SELECT 'customer-' || value, CASE value % 3 WHEN 0 THEN 'IT' WHEN 1 THEN 'UK' ELSE 'FR' END
            FROM {Series};

            INSERT INTO region_totals (Region, Total)
            SELECT 'region-' || (value % 5), SUM(value * 1.5) FROM {Series} GROUP BY value % 5;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>This provider build has no <c>generate_series</c>, so the rows come from a recursive CTE.</summary>
    private const string Series =
        "(WITH RECURSIVE series(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM series WHERE value < 200) SELECT value FROM series)";
}
