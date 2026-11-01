using System.Net;
using EtlPipelines.Server.Contracts;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;

namespace EtlPipelines.Server.Catalog;

/// <summary>
/// Takes a <c>.nupkg</c> a user uploaded, checks it is a pipeline package (a .NET tool), and pushes
/// it to the package feed - after which it can be installed like any other package.
/// </summary>
/// <remarks>
/// The Server pushes it, not the browser: the feed's address is internal to the deployment (inside
/// the compose network), and the same feed URL the agents install from is the one used.
/// </remarks>
public sealed class PackageUploader(PackageCatalogService catalogService, INuGetFeedClient feedClient, IConfiguration configuration)
{
    /// <summary>The configuration key of the API key sent to the feed when pushing.</summary>
    public const string ApiKeySetting = "NuGetFeed:ApiKey";

    // What a .NET tool package declares in its nuspec - the only kind an agent can install and run.
    private const string DotnetToolType = "DotnetTool";

    /// <summary>Validates and pushes the package in <paramref name="package"/>.</summary>
    /// <exception cref="PackageUploadException">The package is not a tool, already exists, or the feed refused it.</exception>
    public async Task<UploadedPackageDto> UploadAsync(Stream package, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"etlpipelines-upload-{Guid.NewGuid():N}.nupkg");
        try
        {
            await using (var file = File.Create(path))
            {
                await package.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var identity = ReadIdentity(path);

            var feedUrl = (await catalogService.GetFeedUrlsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new PackageUploadException(PackageUploadError.FeedUnavailable, "No package feed is configured.");

            try
            {
                await feedClient.PushAsync(feedUrl, path, configuration[ApiKeySetting] ?? "any", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or FatalProtocolException)
            {
                throw IsConflict(exception)
                    ? new PackageUploadException(
                        PackageUploadError.AlreadyExists, $"{identity.Id} {identity.Version.ToNormalizedString()} is already on the feed.", exception)
                    : new PackageUploadException(
                        PackageUploadError.FeedUnavailable, $"The package feed refused the package: {exception.Message}", exception);
            }

            return new UploadedPackageDto(identity.Id, identity.Version.ToNormalizedString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The package's id and version, once it is shown to be a valid package that is a .NET tool.</summary>
    private static PackageIdentity ReadIdentity(string path)
    {
        try
        {
            using var reader = new PackageArchiveReader(path);
            var identity = reader.GetIdentity();

            if (!reader.NuspecReader.GetPackageTypes().Any(type => string.Equals(type.Name, DotnetToolType, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PackageUploadException(
                    PackageUploadError.Invalid,
                    $"{identity.Id} {identity.Version.ToNormalizedString()} is not a .NET tool package. A pipeline package is a console application packed with <PackAsTool>true</PackAsTool>.");
            }

            return identity;
        }
        catch (Exception exception) when (exception is InvalidDataException or PackagingException or FileNotFoundException or ArgumentException)
        {
            // The exception's own message names the server's temporary file; the caller gets the reason without it.
            throw new PackageUploadException(
                PackageUploadError.Invalid, "The file is not a valid NuGet package (.nupkg).", exception);
        }
    }

    private static bool IsConflict(Exception exception) =>
        exception is HttpRequestException { StatusCode: HttpStatusCode.Conflict }
        || exception.InnerException is HttpRequestException { StatusCode: HttpStatusCode.Conflict }
        || exception.Message.Contains("409", StringComparison.Ordinal);
}
