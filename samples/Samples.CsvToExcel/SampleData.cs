using System.IO.Abstractions;

namespace EtlPipelines.Samples.CsvToExcel;

/// <summary>
/// Stands in for whatever normally drops the file: an export, an upload, a partner feed.
/// </summary>
/// <remarks>Kept out of the sample itself, where it would be the longest thing on the page.</remarks>
internal static class SampleData
{
    public static Task WriteSalesAsync(IFileInfo file, CancellationToken cancellationToken)
    {
        var lines = new List<string> { "Id,Region,Product,Amount" };

        for (var i = 1; i <= 500; i++)
        {
            // Every tenth row is a refund, which the pipeline filters out.
            var amount = i % 10 == 0 ? -i : i * 1.25m;
            lines.Add($"{i},region-{i % 5},product-{i % 20},{amount}");
        }

        return file.WriteAllLinesAsync(lines, cancellationToken);
    }
}
