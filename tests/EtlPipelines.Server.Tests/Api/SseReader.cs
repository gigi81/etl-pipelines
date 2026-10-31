namespace EtlPipelines.Server.Tests.Api;

/// <summary>Reads server-sent events off a response stream.</summary>
internal sealed class SseReader(Stream stream) : IDisposable
{
    private readonly StreamReader _reader = new(stream);

    /// <summary>The next event's type and data; <see langword="null"/> when the stream has ended.</summary>
    public async Task<(string Type, string Data)?> ReadAsync(CancellationToken cancellationToken)
    {
        var type = "message";
        string? data = null;

        while (await _reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                type = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data = line["data:".Length..].Trim();
            }
            else if (line.Length == 0 && data is not null)
            {
                return (type, data);
            }
        }

        return null;
    }

    /// <summary>The next event, failing the test if none arrives within <paramref name="timeout"/>.</summary>
    public async Task<(string Type, string Data)> ReadWithinAsync(TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            return await ReadAsync(cancel.Token) ?? throw new InvalidOperationException("The stream ended before an event arrived.");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No event arrived within {timeout}.");
        }
    }

    /// <summary>Whether nothing arrives within <paramref name="duration"/>.</summary>
    public async Task<bool> StaysQuietForAsync(TimeSpan duration)
    {
        using var cancel = new CancellationTokenSource(duration);
        try
        {
            await ReadAsync(cancel.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public void Dispose() => _reader.Dispose();
}
