using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Server.Tests.Api;

/// <summary>
/// Just enough of a NuGet v3 feed to be pushed to: a service index that advertises a publish
/// endpoint, and that endpoint, which records what it received and answers as configured. So the real
/// NuGet client - and its real errors - is what the tests exercise.
/// </summary>
internal sealed class FakeNuGetFeed : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeNuGetFeed(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>The feed's service index URL.</summary>
    public string Url { get; }

    /// <summary>Every push received: the API key header and the request body.</summary>
    public List<(string? ApiKey, byte[] Body)> Pushes { get; } = [];

    /// <summary>The packages the feed offers, by id, with their versions - what searching and looking up versions answer with.</summary>
    public Dictionary<string, string[]> Packages { get; } = [];

    /// <summary>What pushing answers with. <see cref="HttpStatusCode.Created"/> by default.</summary>
    public HttpStatusCode PushStatus { get; set; } = HttpStatusCode.Created;

    public static async Task<FakeNuGetFeed> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        FakeNuGetFeed? feed = null;
        app.MapGet("/v3/index.json", (HttpRequest request) => Results.Json(new
        {
            version = "3.0.0",
            resources = new[]
            {
                new { Id = $"http://{request.Host}/query", Type = "SearchQueryService/3.4.0" },
                new { Id = $"http://{request.Host}/flat/", Type = "PackageBaseAddress/3.0.0" },
                new { Id = $"http://{request.Host}/api/v2/package", Type = "PackagePublish/2.0.0" },
            }.Select(r => new Dictionary<string, string> { ["@id"] = r.Id, ["@type"] = r.Type }),
        }));
        app.MapGet("/query", (string? q) => Results.Json(new Dictionary<string, object>
        {
            ["totalHits"] = feed!.Packages.Count,
            ["data"] = feed.Packages
                .Where(package => string.IsNullOrEmpty(q) || package.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(package => new Dictionary<string, object>
                {
                    ["id"] = package.Key,
                    ["version"] = package.Value.Last(),
                    ["versions"] = package.Value.Select(version => new Dictionary<string, object> { ["version"] = version, ["downloads"] = 0 }).ToArray(),
                })
                .ToArray(),
        }));
        app.MapGet("/flat/{id}/index.json", (string id) =>
            feed!.Packages.FirstOrDefault(package => string.Equals(package.Key, id, StringComparison.OrdinalIgnoreCase)) is { Value: not null } found
                ? Results.Json(new { versions = found.Value })
                : Results.NotFound());
        app.MapPut("/api/v2/package", async (HttpRequest request) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            feed!.Pushes.Add((request.Headers["X-NuGet-ApiKey"].FirstOrDefault(), body.ToArray()));
            return Results.StatusCode((int)feed.PushStatus);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        feed = new FakeNuGetFeed(app, $"{address}/v3/index.json");
        return feed;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
