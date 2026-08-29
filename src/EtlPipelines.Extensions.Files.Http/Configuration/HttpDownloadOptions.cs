using EtlPipelines.Files.Configuration;

namespace EtlPipelines.Files.Http.Configuration;

/// <summary>Settings for downloading one or more files over HTTP.</summary>
public sealed class HttpDownloadOptions : FileWriteOptions
{
    /// <summary>
    /// How long a single file's request may run before it is abandoned. Defaults to
    /// <see langword="null"/>, meaning the named <see cref="HttpClient"/>'s own
    /// <see cref="HttpClient.Timeout"/> applies unmodified.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Escape hatch for headers, authentication, or anything else a request needs beyond the named client's own defaults.</summary>
    public Action<HttpRequestMessage>? ConfigureRequest { get; set; }

    /// <summary>
    /// Whether the first part of a failed response's body is included in the error description.
    /// Defaults to <see langword="false"/> - useful for debugging an API gateway, off by default
    /// because an error body can carry a token or other secret that does not belong in a log.
    /// </summary>
    public bool IncludeResponseBodyInErrors { get; set; }
}
