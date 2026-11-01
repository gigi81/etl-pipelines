using EtlPipelines.Server.Database;
using EtlPipelines.Server.Live.Events;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Server.Live;

/// <summary>
/// Loads <see cref="LiveState"/> from the database when the server starts, then writes what happens
/// to it back, behind: every <see cref="LiveStateOptions.SyncInterval"/>, if any events are waiting,
/// they are taken and written in one batch (<see cref="LiveEventWriter"/>). Once more on shutdown, so
/// a clean stop loses nothing.
/// </summary>
/// <remarks>
/// A batch that fails to write goes back to the front of the outbox and is tried again next time.
/// If it keeps failing the events are written one at a time, so a single event the database
/// refuses cannot hold up everything behind it forever: that one is logged and dropped.
/// </remarks>
public sealed class LiveStateSync(
    IServiceScopeFactory scopeFactory,
    LiveState liveState,
    IOptions<LiveStateOptions> options,
    TimeProvider timeProvider,
    ILogger<LiveStateSync> logger) : BackgroundService
{
    /// <summary>How many times in a row a batch may fail before its events are written one by one.</summary>
    private const int BatchAttempts = 3;

    private readonly SemaphoreSlim _flushing = new(1, 1);
    private int _failedAttempts;

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Before anything else: requests wait for this to finish.
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            await liveState.LoadAsync(scope.ServiceProvider.GetRequiredService<ServerDbContext>(), cancellationToken).ConfigureAwait(false);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SyncInterval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushSafelyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Whatever happened since the last tick - the shutdown flush is not cancelled by the host
        // giving up on the timer.
        await FlushSafelyAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never stops the loop: the events are back in the outbox, and the next tick tries again.
            logger.LogError(exception, "Writing the live state to the database failed unexpectedly.");
        }
    }

    /// <summary>
    /// Writes every event waiting to the database now. Returns how many it wrote. Called on a timer and
    /// on shutdown; tests and anything that must see the database caught up can call it too.
    /// </summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        await _flushing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = liveState.DrainPendingEvents();
            if (batch.Count == 0)
            {
                return 0;
            }

            try
            {
                await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
                liveState.MarkWritten(batch);
                _failedAttempts = 0;
                logger.LogDebug("Wrote {Count} live event(s) to the database.", batch.Count);
                return batch.Count;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failedAttempts++;
                if (_failedAttempts < BatchAttempts)
                {
                    logger.LogWarning(
                        exception, "Writing {Count} live event(s) failed (attempt {Attempt}); they will be tried again.", batch.Count, _failedAttempts);
                    liveState.RequeuePendingEvents(batch);
                    return 0;
                }

                logger.LogError(exception, "Writing {Count} live event(s) failed {Attempts} times; writing them one by one.", batch.Count, _failedAttempts);
                _failedAttempts = 0;
                return await WriteOneByOneAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushing.Release();
        }
    }

    private async Task<int> WriteOneByOneAsync(IReadOnlyList<LiveEvent> batch, CancellationToken cancellationToken)
    {
        var written = 0;
        foreach (var liveEvent in batch)
        {
            try
            {
                await WriteAsync([liveEvent], cancellationToken).ConfigureAwait(false);
                liveState.MarkWritten([liveEvent]);
                written++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The database refused {Event}; dropping it.", liveEvent);
                liveState.MarkWritten([liveEvent]);
            }
        }

        return written;
    }

    private async Task WriteAsync(IReadOnlyList<LiveEvent> events, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var skipped = await LiveEventWriter
            .WriteAsync(scope.ServiceProvider.GetRequiredService<ServerDbContext>(), events, cancellationToken)
            .ConfigureAwait(false);

        if (skipped > 0)
        {
            logger.LogWarning("{Skipped} live event(s) were about rows the database does not have and were skipped.", skipped);
        }
    }
}
