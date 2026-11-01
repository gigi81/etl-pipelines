using EtlPipelines.AgentExecution.V1;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EtlPipelines.Agent;

/// <summary>
/// Keeps this agent connected to the server for as long as it runs: registers (as the same agent every time -
/// see <see cref="AgentIdentity"/>), holds <c>AgentService.Subscribe</c> open to receive work items, and sends a
/// heartbeat on its own timer. When the connection is lost - the server restarting, the network dropping, the
/// server no longer knowing this agent - it connects again after a pause that grows with each failure, instead of
/// stopping the process. The work items themselves are carried out by the <see cref="IWorkItemProcessor"/> (<see cref="WorkItemProcessor"/>), which
/// belongs to the agent rather than to a connection, so a run in progress is not disturbed by any of this.
/// </summary>
public sealed class AgentRegistration(
    AgentService.AgentServiceClient client,
    IWorkItemProcessor processor,
    IOptions<AgentOptions> options,
    ILogger<AgentRegistration> logger) : BackgroundService
{
    /// <summary>How many heartbeats in a row can fail before the connection is given up on.</summary>
    private const int MaxHeartbeatFailures = 3;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    // Kept in memory too, once issued: if the id file cannot be written, a reconnect must not become a new agent.
    private Guid? _agentId;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = options.Value.ReconnectInitialDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            var connected = false;

            try
            {
                await ConnectAsync(() => connected = true, stoppingToken).ConfigureAwait(false);
                logger.LogWarning("The server ended the connection.");
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Lost the server: {Reason}", Describe(exception));
                logger.LogDebug(exception, "Details of the lost connection");
            }

            // A connection that was made resets the pause: the next failure is a new one, not one more of a long run of them.
            if (connected)
            {
                delay = options.Value.ReconnectInitialDelay;
            }

            logger.LogInformation("Connecting again in {Delay}.", delay);

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.Value.ReconnectMaxDelay.Ticks));
        }

        // Stopping: what is still running was given the same token and is winding down.
        await processor.WhenIdleAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// One connection: registers, then receives work until the stream ends or fails, or the heartbeats show the
    /// server is gone or no longer knows this agent. Returns when the server closes the stream; otherwise throws.
    /// </summary>
    private async Task ConnectAsync(Action onRegistered, CancellationToken stoppingToken)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        string? lostReason = null;

        var agentId = await RegisterAsync(connection.Token).ConfigureAwait(false);
        onRegistered();

        var heartbeats = SendHeartbeatsAsync(
            agentId,
            reason =>
            {
                lostReason = reason;
                connection.Cancel();
            },
            connection.Token);

        try
        {
            using var call = client.Subscribe(new SubscribeRequest { AgentId = agentId }, cancellationToken: connection.Token);

            await foreach (var workItem in call.ResponseStream.ReadAllAsync(connection.Token).ConfigureAwait(false))
            {
                // Handed over, not awaited: it runs under the agent's own token, whatever becomes of this connection.
                processor.Process(agentId, workItem, stoppingToken);
            }
        }
        catch (Exception) when (lostReason is not null && !stoppingToken.IsCancellationRequested)
        {
            throw new IOException(lostReason);
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            await heartbeats.ConfigureAwait(false);
        }
    }

    private async Task<string> RegisterAsync(CancellationToken cancellationToken)
    {
        var request = new RegisterAgentRequest
        {
            MachineName = string.IsNullOrWhiteSpace(options.Value.Name) ? Environment.MachineName : options.Value.Name.Trim(),
            Version = AgentVersion.Current,
        };
        request.Tags.AddRange(options.Value.Tags);
        request.HostInfo = HostInfo.Describe();

        // The id from the last start or connection, if there was one: the server then knows this as the same
        // agent rather than issuing a new id (and a new entry in the agents list) every time.
        var previousId = _agentId ?? AgentIdentity.Load(options.Value.CacheDirectory);
        if (previousId is { } known)
        {
            request.AgentId = known.ToString();
        }

        var response = await client
            .RegisterAgentAsync(request, deadline: DateTime.UtcNow + CallTimeout, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var agentId = response.AgentId;

        if (Guid.TryParse(agentId, out var issued))
        {
            _agentId = issued;

            if (issued != previousId && !AgentIdentity.Save(options.Value.CacheDirectory, issued))
            {
                logger.LogWarning(
                    "Could not save the agent id under {CacheDirectory}; this agent will register as a new one the next time it starts.",
                    options.Value.CacheDirectory);
            }
        }

        if (previousId is null)
        {
            logger.LogInformation("Registered as new agent {AgentId}", agentId);
        }
        else
        {
            logger.LogInformation("Registered again as agent {AgentId}", agentId);
        }

        return agentId;
    }

    /// <summary>
    /// Heartbeats until the connection ends. Gives the connection up (by calling <paramref name="lost"/>) when the
    /// server does not know this agent - it was removed, or lost it in a restart - or when several heartbeats in a
    /// row fail, which is how a connection that died without telling anyone is noticed.
    /// </summary>
    private async Task SendHeartbeatsAsync(string agentId, Action<string> lost, CancellationToken connectionToken)
    {
        using var timer = new PeriodicTimer(options.Value.HeartbeatInterval);
        var failures = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(connectionToken).ConfigureAwait(false))
            {
                try
                {
                    await client
                        .HeartbeatAsync(
                            new AgentHeartbeatRequest { AgentId = agentId },
                            deadline: DateTime.UtcNow + CallTimeout,
                            cancellationToken: connectionToken)
                        .ConfigureAwait(false);
                    failures = 0;
                }
                catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
                {
                    lost("the server does not know this agent");
                    return;
                }
                catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled && ++failures >= MaxHeartbeatFailures)
                {
                    lost($"{failures} heartbeats failed ({Describe(exception)})");
                    return;
                }
                catch (RpcException exception) when (exception.StatusCode != StatusCode.Cancelled)
                {
                    logger.LogDebug(exception, "A heartbeat failed; {Failures} so far.", failures);
                }
            }
        }
        catch (Exception) when (connectionToken.IsCancellationRequested)
        {
            // The connection ended: nothing more to send.
        }
    }

    private static string Describe(Exception exception) =>
        exception is RpcException rpc ? $"{rpc.StatusCode}: {rpc.Status.Detail}" : exception.Message;
}
