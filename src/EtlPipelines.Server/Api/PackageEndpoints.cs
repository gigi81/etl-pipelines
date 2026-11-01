using EtlPipelines.Server.Catalog;
using EtlPipelines.Server.Contracts;
using Grpc.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NuGet.Versioning;

namespace EtlPipelines.Server.Api;

/// <summary>
/// The package routes of the REST API: what the feed offers and what is installed, installing,
/// updating and uninstalling, and uploading a package to the feed. Installing is carried out by an
/// agent, so it takes as long as the agent does.
/// </summary>
internal static class PackageEndpoints
{
    /// <summary>The largest package that can be uploaded. A tool with its dependencies is typically tens of megabytes.</summary>
    internal const long MaxUploadBytes = 512L * 1024 * 1024;

    /// <summary>Maps the <c>/packages</c> routes onto <paramref name="api"/>.</summary>
    public static void Map(RouteGroupBuilder api)
    {
        var packages = api.MapGroup("/packages");

        packages.MapGet("/", ListAsync);
        packages.MapPost("/upload", UploadAsync).WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));
        packages.MapPost("/{packageId}/install", InstallAsync);
        packages.MapPost("/{packageId}/update", UpdateAsync);
        packages.MapDelete("/{packageId}", UninstallAsync);
    }

    private static async Task<Ok<PackagesDto>> ListAsync(
        PackageCatalogService catalog, ILoggerFactory loggers, CancellationToken cancellationToken, string? search = null)
    {
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();

        IReadOnlyList<AvailableFeedPackage> onFeed = [];
        string? feedError = null;
        try
        {
            onFeed = await catalog.ListAvailablePackagesAsync(search, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever went wrong reaching the feed (it is down, it answered nonsense, it timed out),
            // what is installed is still worth listing - it can still be updated or removed.
            loggers.CreateLogger(typeof(PackageEndpoints)).LogWarning(exception, "The package feed could not be browsed.");
            feedError = exception.Message;
        }

        var installedPipelines = await catalog.ListInstalledPipelinesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new PackagesDto(Combine(onFeed, installedPipelines, search), feedError));
    }

    /// <summary>The feed's packages and the installed ones, as one list by id.</summary>
    internal static IReadOnlyList<PackageDto> Combine(
        IReadOnlyList<AvailableFeedPackage> onFeed, IReadOnlyList<InstalledPipelineInfo> installedPipelines, string? search)
    {
        var feed = onFeed.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);

        // One entry per installed package: its newest installed version, and that version's pipelines.
        var installed = installedPipelines
            .GroupBy(pipeline => pipeline.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var version = group.Select(pipeline => pipeline.PackageVersion).OrderByDescending(Parse).First();
                    return (Version: version, Pipelines: group.Where(pipeline => pipeline.PackageVersion == version).Select(pipeline => pipeline.Name).Order().ToList());
                },
                StringComparer.OrdinalIgnoreCase);

        var ids = feed.Keys
            .Concat(installed.Keys.Where(id => search is null || id.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        return ids.Select(id =>
        {
            var versions = feed.TryGetValue(id, out var package)
                ? package.Versions.OrderByDescending(Parse).ToList()
                : [];
            var latestStable = versions.FirstOrDefault(version => NuGetVersion.TryParse(version, out var parsed) && !parsed.IsPrerelease);
            installed.TryGetValue(id, out var current);

            var updateAvailable = current.Version is not null
                && latestStable is not null
                && Parse(latestStable) > Parse(current.Version);

            return new PackageDto(id, versions, latestStable, current.Version, updateAvailable, current.Pipelines ?? []);
        }).ToList();
    }

    // Versions that do not parse sort last rather than failing the whole list.
    private static NuGetVersion Parse(string version) =>
        NuGetVersion.TryParse(version, out var parsed) ? parsed : new NuGetVersion(0, 0, 0);

    /// <summary>Installs the <paramref name="version"/> given in the query string - the latest stable one if it is left out.</summary>
    private static async Task<Results<Ok<PackageInstallResultDto>, ProblemHttpResult>> InstallAsync(
        string packageId, PackageOperations operations, CancellationToken cancellationToken, string? version = null)
    {
        try
        {
            var installed = await operations.InstallAsync(packageId, version, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new PackageInstallResultDto(packageId, installed.Version, installed.PipelineNames));
        }
        catch (RpcException exception)
        {
            return ApiProblems.From(exception);
        }
    }

    private static async Task<Results<Ok<PackageInstallResultDto>, ProblemHttpResult>> UpdateAsync(
        string packageId, PackageOperations operations, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await operations.UpdateAsync(packageId, cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(new PackageInstallResultDto(packageId, updated.Version, updated.PipelineNames));
        }
        catch (RpcException exception)
        {
            return ApiProblems.From(exception);
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> UninstallAsync(
        string packageId, PackageOperations operations, CancellationToken cancellationToken)
    {
        try
        {
            await operations.UninstallAsync(packageId, cancellationToken).ConfigureAwait(false);
            return TypedResults.NoContent();
        }
        catch (RpcException exception)
        {
            return ApiProblems.From(exception);
        }
    }

    /// <summary>Receives a <c>.nupkg</c> as the raw request body and pushes it to the feed.</summary>
    private static async Task<Results<Created<UploadedPackageDto>, ProblemHttpResult>> UploadAsync(
        HttpRequest request, PackageUploader uploader, CancellationToken cancellationToken)
    {
        if (request.ContentLength == 0)
        {
            return TypedResults.Problem(detail: "The request has no package in it.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var uploaded = await uploader.UploadAsync(request.Body, cancellationToken).ConfigureAwait(false);
            return TypedResults.Created("/api/packages", uploaded);
        }
        catch (PackageUploadException exception)
        {
            var status = exception.Error switch
            {
                PackageUploadError.Invalid => StatusCodes.Status400BadRequest,
                PackageUploadError.AlreadyExists => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status502BadGateway,
            };

            return TypedResults.Problem(detail: exception.Message, statusCode: status);
        }
    }
}
