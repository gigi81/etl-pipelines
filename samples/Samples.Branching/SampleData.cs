using System.Globalization;
using System.IO.Abstractions;

namespace EtlPipelines.Samples.Branching;

/// <summary>
/// Stands in for the sensor feed this job would normally be reading.
/// </summary>
/// <remarks>Kept out of the sample itself, where the branch is the only thing worth looking at.</remarks>
internal static class SampleData
{
    public const int Rows = 2_000;

    public static Task WriteReadingsAsync(IFileInfo file, CancellationToken cancellationToken)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<string> { "SensorId,TakenAt,Celsius" };

        for (var i = 0; i < Rows; i++)
        {
            // A cold snap in the middle, so the filtered branch genuinely drops rows.
            var celsius = Math.Round(Math.Sin(i / 50.0) * 20, 2);
            lines.Add($"{i % 8},{start.AddMinutes(i):yyyy-MM-ddTHH:mm:ss},{celsius.ToString(CultureInfo.InvariantCulture)}");
        }

        return file.WriteAllLinesAsync(lines, cancellationToken);
    }
}
