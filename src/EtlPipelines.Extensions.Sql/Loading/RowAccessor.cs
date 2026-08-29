using System.Reflection;

namespace EtlPipelines.Extensions.Sql.Loading;

/// <summary>
/// The columns a row type maps to, and how to read each one out of an instance.
/// </summary>
/// <remarks>
/// Built once per row type and shared, because reflecting per row is exactly the cost an ETL load
/// cannot afford. Both write paths use it: the parameterised INSERT binds these values to parameters,
/// and <see cref="BatchDataReader{TRow}"/> presents them as columns to a provider's bulk loader.
/// </remarks>
/// <typeparam name="TRow">The row type being written.</typeparam>
internal static class RowAccessor<TRow>
{
    private static readonly Lazy<(string[] Columns, Func<TRow, object?>[] Readers)> Map = new(Build);

    /// <summary>The column names, in the order the readers return their values.</summary>
    public static string[] Columns => Map.Value.Columns;

    /// <summary>One accessor per column, in the same order.</summary>
    public static Func<TRow, object?>[] Readers => Map.Value.Readers;

    /// <summary>The subset of columns a caller asked for, keeping the row type's order.</summary>
    public static (string[] Columns, Func<TRow, object?>[] Readers) For(IReadOnlyCollection<string>? wanted)
    {
        if (wanted is null || wanted.Count == 0)
        {
            return (Columns, Readers);
        }

        var chosen = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var columns = new List<string>(wanted.Count);
        var readers = new List<Func<TRow, object?>>(wanted.Count);

        for (var i = 0; i < Columns.Length; i++)
        {
            if (chosen.Contains(Columns[i]))
            {
                columns.Add(Columns[i]);
                readers.Add(Readers[i]);
            }
        }

        return ([.. columns], [.. readers]);
    }

    private static (string[], Func<TRow, object?>[]) Build()
    {
        var properties = typeof(TRow)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        var columns = new string[properties.Length];
        var readers = new Func<TRow, object?>[properties.Length];

        for (var i = 0; i < properties.Length; i++)
        {
            var property = properties[i];
            columns[i] = property.Name;

            // DBNull rather than null: a parameter left as a CLR null is not the same thing to a
            // provider as one explicitly set to the database's null.
            readers[i] = row => property.GetValue(row) ?? DBNull.Value;
        }

        return (columns, readers);
    }
}
