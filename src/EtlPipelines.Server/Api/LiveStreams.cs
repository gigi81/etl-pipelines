using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EtlPipelines.Server.Contracts;
using EtlPipelines.Server.Live;

namespace EtlPipelines.Server.Api;

/// <summary>
/// The server-sent-event streams of the REST API. Both are driven by <see cref="LiveState"/>'s change
/// notices: they sleep until something changes, send it, and sleep again - there is no timer that
/// looks for changes, and nothing on these paths queries the database for anything in flight. The
/// only timer is the keep-alive, an empty <c>ping</c> event so an idle connection is not closed by a
/// proxy.
/// </summary>
internal static class LiveStreams
{
    /// <summary>How long a stream goes without sending anything before it sends a <c>ping</c>.</summary>
    internal static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly SseItem<string> Ping = new("{}", "ping");

    private static readonly SseItem<string> Ready = new("{}", "ready");

    /// <summary>
    /// Everything the lists show, as it changes: <c>run</c> (a <see cref="RunSummaryDto"/>), <c>agent</c>
    /// (an <see cref="AgentDto"/>), <c>agent-removed</c> (an agent is no longer listed) and <c>pipelines</c> (the installed pipelines changed; refetch them).
    /// It sends changes only, not a starting point: a client fetches the lists itself, and again each
    /// time the stream (re)connects. The first event, <c>ready</c>, is sent at once: it makes the
    /// response start (ASP.NET holds the headers back until there is something to send) and tells the
    /// client the stream is listening, so a refetch after it cannot miss a change.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> EventsAsync(
        LiveState live, LiveSubscription subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            yield return Ready;

            while (!cancellationToken.IsCancellationRequested)
            {
                var changes = await subscription.NextAsync(KeepAlive, cancellationToken).ConfigureAwait(false);
                if (changes.Count == 0)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        yield return Ping;
                    }

                    continue;
                }

                foreach (var change in changes)
                {
                    SseItem<string>? item = change.Kind switch
                    {
                        LiveChangeKind.Run => live.TryGetRunSummary(change.Id) is { } run ? Item("run", run) : null,
                        LiveChangeKind.Agent => live.TryGetAgent(change.Id) is { } agent ? Item("agent", agent) : null,
                        LiveChangeKind.AgentRemoved => Item("agent-removed", new { id = change.Id }),
                        _ => new SseItem<string>("{}", "pipelines"),
                    };

                    if (item is { } send)
                    {
                        yield return send;
                    }
                }
            }
        }
        finally
        {
            subscription.Dispose();
        }
    }

    /// <summary>
    /// One run, live: its full detail (<c>run</c> events) right away - <paramref name="first"/> - and
    /// again each time it changes, until the run has finished.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> RunAsync(
        LiveState live, LiveSubscription subscription, Guid runId, RunDetailDto first, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            var detail = first;
            while (true)
            {
                yield return Item("run", detail);

                if (!RunQueries.IsActive(detail.Run.Status))
                {
                    yield break;
                }

                // Wait for this run to change; a quiet minute only earns a ping.
                RunDetailDto? next = null;
                while (next is null)
                {
                    var changes = await subscription.NextAsync(KeepAlive, cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        yield break;
                    }

                    if (changes.Count == 0)
                    {
                        yield return Ping;
                        continue;
                    }

                    next = live.TryGetRun(runId);
                    if (next is null)
                    {
                        yield break;
                    }
                }

                detail = next;
            }
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private static SseItem<string> Item<T>(string eventType, T payload) =>
        new(JsonSerializer.Serialize(payload, Json), eventType);
}
