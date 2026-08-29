using System.Globalization;
using System.Reflection;
using MiniExcelLib.Core.Attributes;

namespace EtlPipelines.Extensions.Excel;

/// <summary>
/// Turns one worksheet row, arriving as a column-name to cell-value map, into a row object.
/// </summary>
/// <remarks>
/// <para>
/// MiniExcel can do this itself through its typed <c>Query&lt;T&gt;</c>, and that path is both faster
/// and better at edge cases than anything written here — but it converts inside the enumerator, so a
/// single cell that will not convert throws out of the iterator, and an iterator that has thrown
/// cannot be resumed. Skipping one bad row would therefore mean abandoning the rest of the sheet.
/// </para>
/// <para>
/// Reading the untyped stream and converting a row at a time moves that boundary inside the loop,
/// which is what lets a bad row be counted, dead-lettered and stepped over — the same tolerance the
/// CSV connector offers. The cost is that conversion is this file's problem rather than MiniExcel's.
/// </para>
/// </remarks>
/// <typeparam name="TRow">The row type to build.</typeparam>
internal static class ExcelRowMapper<TRow>
    where TRow : class, new()
{
    /// <summary>
    /// Column name to setter, built once per row type. Names are matched without regard to case
    /// because a spreadsheet header is typed by a person.
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, PropertyInfo>> Columns = new(BuildMap);

    /// <summary>Builds a row, or reports the first cell that would not convert.</summary>
    public static ErrorOr<TRow> Map(IDictionary<string, object?> cells, CultureInfo culture)
    {
        var row = new TRow();

        foreach (var cell in cells)
        {
            if (!Columns.Value.TryGetValue(cell.Key, out var property))
            {
                // A column the row type does not mention is not an error: a sheet may carry more than
                // this particular pipeline cares about.
                continue;
            }

            var converted = Convert(cell.Value, property.PropertyType, culture);
            if (converted.IsError)
            {
                return Error.Validation(
                    "excel.cell_not_convertible",
                    $"Column '{cell.Key}' holds {Describe(cell.Value)}, which cannot be read as " +
                    $"{property.PropertyType.Name} for {typeof(TRow).Name}.{property.Name}.");
            }

            property.SetValue(row, converted.Value);
        }

        return row;
    }

    /// <summary>Renders a row as text, so a rejected one stays recoverable from the dead-letter sink.</summary>
    public static string Describe(IDictionary<string, object?> cells) =>
        string.Join(", ", cells.Select(c => $"{c.Key}={c.Value}"));

    private static string Describe(object? value) =>
        value is null ? "no value" : $"'{value}' ({value.GetType().Name})";

    private static IReadOnlyDictionary<string, PropertyInfo> BuildMap()
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in typeof(TRow).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (property.GetCustomAttribute<MiniExcelIgnoreAttribute>() is { Ignore: true })
            {
                continue;
            }

            // MiniExcel's own column attributes are honoured, so a row type written for its typed
            // reader keeps working when it is read through this one.
            var named = property.GetCustomAttribute<MiniExcelColumnNameAttribute>();
            if (named is not null)
            {
                map[named.Name] = property;
                foreach (var alias in named.Aliases ?? [])
                {
                    map[alias] = property;
                }

                continue;
            }

            map[property.Name] = property;
        }

        return map;
    }

    /// <summary>Converts one cell value to the property's type.</summary>
    /// <remarks>
    /// Everything numeric comes out of a worksheet as <see cref="double"/> and everything textual as
    /// <see cref="string"/>, so most of the work is narrowing rather than parsing.
    /// </remarks>
    private static ErrorOr<object?> Convert(object? value, Type target, CultureInfo culture)
    {
        if (value is null or DBNull)
        {
            // An empty cell leaves the property at its default. A non-nullable target is not treated
            // as an error, because a blank trailing column is ordinary in a spreadsheet.
            return default(object);
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (underlying.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            if (underlying == typeof(string))
            {
                return System.Convert.ToString(value, culture);
            }

            if (underlying.IsEnum)
            {
                return value is string text
                    ? Enum.Parse(underlying, text, ignoreCase: true)
                    : Enum.ToObject(underlying, System.Convert.ToInt64(value, culture));
            }

            if (underlying == typeof(Guid))
            {
                return Guid.Parse(System.Convert.ToString(value, culture)!);
            }

            if (underlying == typeof(DateTime))
            {
                // A date that was never formatted as one arrives as its serial number.
                return value is double serial
                    ? DateTime.FromOADate(serial)
                    : System.Convert.ToDateTime(value, culture);
            }

            if (underlying == typeof(DateOnly))
            {
                var date = value is double d ? DateTime.FromOADate(d) : System.Convert.ToDateTime(value, culture);
                return DateOnly.FromDateTime(date);
            }

            if (underlying == typeof(TimeOnly))
            {
                var time = value is double d ? DateTime.FromOADate(d) : System.Convert.ToDateTime(value, culture);
                return TimeOnly.FromDateTime(time);
            }

            if (underlying == typeof(TimeSpan))
            {
                return value is double d ? TimeSpan.FromDays(d) : TimeSpan.Parse(System.Convert.ToString(value, culture)!, culture);
            }

            return System.Convert.ChangeType(value, underlying, culture);
        }
        catch (Exception exception) when (exception
            is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            return Error.Validation("excel.cell_not_convertible", exception.Message);
        }
    }
}
