using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Building;
using EtlPipelines.Files.Http.Configuration;
using EtlPipelines.Files.Http.Stages;

namespace EtlPipelines.Files.Http;

/// <summary>Downloading files over HTTP as a pipeline stage.</summary>
public static class Extensions
{
    /// <summary>Downloads one file.</summary>
    public static IPipelineBuilder DownloadFromHttp(
        this IPipelineBuilder builder,
        string clientName,
        Uri url,
        IFileInfo target,
        Action<HttpDownloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(target);

        return builder.DownloadFromHttp(clientName, [new HttpDownload(url, target)], configure);
    }

    /// <summary>
    /// Downloads every URL in <paramref name="urls"/> into <paramref name="targetDirectory"/>, naming
    /// each file after its URL's last path segment.
    /// </summary>
    public static IPipelineBuilder DownloadFromHttp(
        this IPipelineBuilder builder,
        string clientName,
        IDirectoryInfo targetDirectory,
        IEnumerable<Uri> urls,
        Action<HttpDownloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ArgumentNullException.ThrowIfNull(urls);

        var downloads = new List<HttpDownload>();

        foreach (var url in urls)
        {
            var name = FileNameFor(url);

            if (name.IsError)
            {
                throw new ArgumentException(name.FirstError.Description, nameof(urls));
            }

            downloads.Add(new HttpDownload(url, targetDirectory.File(name.Value)));
        }

        return builder.DownloadFromHttp(clientName, downloads, configure);
    }

    /// <summary>Downloads every file in <paramref name="downloads"/>.</summary>
    public static IPipelineBuilder DownloadFromHttp(
        this IPipelineBuilder builder,
        string clientName,
        IEnumerable<HttpDownload> downloads,
        Action<HttpDownloadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(downloads);

        var options = new HttpDownloadOptions();
        configure?.Invoke(options);

        var list = downloads as IReadOnlyList<HttpDownload> ?? [.. downloads];

        return builder.AddStage(new HttpDownloadStage(clientName, list, options));
    }

    /// <summary>
    /// Derives a safe file name from a URL's last path segment - the same rule Zip Slip enforces on
    /// an archive entry, because a server choosing where a client writes on disk is the same problem
    /// wearing a different hat.
    /// </summary>
    private static ErrorOr<string> FileNameFor(Uri url)
    {
        if (url.Segments.Length == 0)
        {
            return Error.Failure("http.download.bad_url", $"'{url}' has no path segment to name a file from.");
        }

        var raw = Uri.UnescapeDataString(url.Segments[^1].TrimEnd('/'));

        if (string.IsNullOrEmpty(raw) || raw is "." or ".." || raw.Contains('/') || raw.Contains('\\'))
        {
            return Error.Failure(
                "http.download.bad_url",
                $"'{url}' does not name a single file - its last segment is '{raw}'.");
        }

        return raw;
    }
}
