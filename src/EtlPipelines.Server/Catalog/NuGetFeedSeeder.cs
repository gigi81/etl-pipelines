using EtlPipelines.Server.Database;
using EtlPipelines.Server.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EtlPipelines.Server.Catalog;

/// <summary>
/// Ensures <c>NuGetFeeds</c> holds the one row SERVER.md's "Package feed" decision calls for,
/// reading the URL from configuration (<c>NuGetFeed:Url</c>) - upserted on every startup, so a
/// first run needs no separate manual step and a later change to the configured URL just updates
/// the existing row.
/// </summary>
/// <remarks>
/// An <see cref="IHostedService"/>, not something <see cref="ServerApplication.Build"/> itself
/// does: hosted services only start once <c>RunAsync()</c>/<c>StartAsync()</c> actually runs the
/// host, never merely from building it - which is what keeps
/// <see cref="EtlPipelines.Server.Tests"/>'s smoke test (build only, never run) from needing a real
/// Postgres to pass.
/// </remarks>
public sealed class NuGetFeedSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<NuGetFeedSeeder> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var url = configuration["NuGetFeed:Url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning(
                "NuGetFeed:Url is not configured - ListAvailablePackages/ListUpdates will find no feed to browse.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ServerDbContext>();

        var feed = await context.NuGetFeeds
            .OrderBy(f => f.Ordinal)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (feed is null)
        {
            context.NuGetFeeds.Add(new NuGetFeed { Id = Guid.NewGuid(), Url = url, Ordinal = 0 });
        }
        else if (feed.Url != url)
        {
            feed.Url = url;
        }
        else
        {
            // Already exactly what configuration asks for - nothing to save.
            return;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
