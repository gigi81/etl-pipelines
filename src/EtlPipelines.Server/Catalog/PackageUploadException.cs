namespace EtlPipelines.Server.Catalog;

/// <summary>A package could not be uploaded to the feed; <see cref="Error"/> says why.</summary>
public sealed class PackageUploadException(PackageUploadError error, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The reason, which callers map onto their own error format.</summary>
    public PackageUploadError Error { get; } = error;
}
