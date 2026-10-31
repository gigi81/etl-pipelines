using EtlPipelines.Server.Database;
using EtlPipelines.Server.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtlPipelines.Server.Tests.Live;

/// <summary>Builds a <see cref="LiveState"/> the way startup does: loaded from a database.</summary>
internal static class TestLiveState
{
    /// <summary>A state loaded from <paramref name="db"/> (an empty one if none is given).</summary>
    public static async Task<LiveState> CreateAsync(ServerDbContext? db = null, TimeProvider? clock = null)
    {
        var live = new LiveState(clock ?? TimeProvider.System, NullLogger<LiveState>.Instance);
        await live.LoadAsync(db ?? SqliteServerDbContext.Create(), CancellationToken.None);
        return live;
    }
}
