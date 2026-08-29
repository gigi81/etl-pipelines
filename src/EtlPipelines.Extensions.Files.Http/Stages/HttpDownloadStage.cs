using System.Diagnostics;
using System.IO.Abstractions;
using ErrorOr;
using EtlPipelines.Abstractions.Execution;
using EtlPipelines.Files.Configuration;
using EtlPipelines.Files.Http.Configuration;
using EtlPipelines.Files.Writing;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Files.Http.Stages;

/// <summary>Downloads one or more files over HTTP.</summary>
public sealed class HttpDownloadStage : IPipelineStage
{
    private readonly string _clientName;
    private readonly IReadOnlyList<HttpDownload> _downloads;
    private readonly HttpDownloadOptions _options;
    private readonly string _publishAs;

    /// <summary>Downloads every file in <paramref name="downloads"/> using the named client <paramref name="clientName"/>.</summary>
    public HttpDownloadStage(string clientName, IReadOnlyList<HttpDownload> downloads, HttpDownloadOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(options);

        _clientName = clientName;
        _downloads = downloads;
        _options = options;

        Name = options.Name ?? (downloads.Count == 1
            ? $"download {LastSegment(downloads[0].Url)}"
            : $"download {downloads.Count} files");

        _publishAs = options.PublishAs ?? Name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<ErrorOr<StageResult>> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.StartNew();

        var factory = context.Services.GetService<IHttpClientFactory>()
            ?? throw new InvalidOperationException(
                $"No IHttpClientFactory is registered. Call services.AddHttpClient(\"{_clientName}\") " +
                "during startup.");

        var client = factory.CreateClient(_clientName);

        var outcomes = new (bool Ok, string? Message)?[_downloads.Count];

        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxConcurrency),
            CancellationToken = stopSource.Token,
        };

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, _downloads.Count), parallelOptions, async (i, token) =>
            {
                try
                {
                    var outcome = await DownloadOneAsync(client, _downloads[i], token).ConfigureAwait(false);
                    outcomes[i] = outcome.IsError ? (false, outcome.FirstError.Description) : (true, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    outcomes[i] = (false, ex.Message);
                }
                catch (HttpRequestException ex)
                {
                    outcomes[i] = (false, ex.Message);
                }
                catch (TaskCanceledException) when (!token.IsCancellationRequested)
                {
                    // A per-file timeout, not the run being cancelled - see DownloadOneAsync, which
                    // races the shared token against a per-file one and cannot itself tell them apart.
                    outcomes[i] = (false, $"Timed out downloading {_downloads[i].Url}.");
                }

                if (outcomes[i] is { Ok: false } && _options.OnFileError == FileErrorAction.Stop)
                {
                    await stopSource.CancelAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own stop-on-first-failure firing, not the caller's cancellation.
        }

        var produced = new List<IFileInfo>();
        var failures = new List<string>();

        for (var i = 0; i < _downloads.Count; i++)
        {
            if (outcomes[i] is not { } outcome)
            {
                continue;
            }

            if (outcome is { Ok: false, Message: { } message })
            {
                failures.Add($"{Name}: file {i + 1} of {_downloads.Count}, {_downloads[i].Url}: {message}");
            }
            else
            {
                produced.Add(_downloads[i].Target);
            }
        }

        context.PublishProducedFiles(_publishAs, produced);

        if (failures.Count > 0)
        {
            return _options.OnFileError == FileErrorAction.Stop
                ? Error.Failure($"http.download.{Name}.failed", failures[0])
                : Error.Failure(
                    $"http.download.{Name}.failed",
                    $"{failures.Count} of {_downloads.Count} files failed: {string.Join("; ", failures)}");
        }

        return new StageResult(Name, 0, 0, 0, started.Elapsed);
    }

    private async ValueTask<ErrorOr<FileWriteOutcome>> DownloadOneAsync(
        HttpClient client, HttpDownload download, CancellationToken cancellationToken)
    {
        CancellationTokenSource? perFile = null;

        if (_options.Timeout is { } timeout)
        {
            perFile = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perFile.CancelAfter(timeout);
        }

        using var perFileDisposable = perFile;
        var token = perFile?.Token ?? cancellationToken;

        using var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
        _options.ConfigureRequest?.Invoke(request);

        // ResponseHeadersRead, not the default: ResponseContentRead buffers the entire body into
        // memory before this returns, which for a large file is the whole problem streaming avoids.
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = string.Empty;

            if (_options.IncludeResponseBodyInErrors)
            {
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                detail = $" {body[..Math.Min(body.Length, 512)]}";
            }

            return Error.Failure(
                $"http.download.{Name}.status",
                $"GET {download.Url} returned {(int)response.StatusCode} {response.ReasonPhrase}.{detail}");
        }

        return await AtomicWrite.WriteAsync(
            download.Target,
            _options.Overwrite,
            _options.CreateTargetDirectory,
            async (outStream, writeToken) =>
            {
                await using var body = await response.Content.ReadAsStreamAsync(writeToken).ConfigureAwait(false);
                await body.CopyToAsync(outStream, writeToken).ConfigureAwait(false);
            },
            token).ConfigureAwait(false);
    }

    private static string LastSegment(Uri url)
    {
        var segment = url.Segments.Length > 0 ? url.Segments[^1].TrimEnd('/') : url.Host;
        return string.IsNullOrEmpty(segment) ? url.Host : Uri.UnescapeDataString(segment);
    }
}
