using NuGet.Common;

namespace EtlPipelines.Server.Catalog;

/// <summary>
/// A NuGet logger that remembers the errors it is told about. Some of NuGet's failures - refusing a
/// source, say - are logged and the call simply returns; without this they would be lost, and a push
/// that did nothing would look like one that worked.
/// </summary>
internal sealed class NuGetErrorLogger : LoggerBase
{
    private readonly List<string> _errors = [];

    /// <summary>The messages logged at error level so far.</summary>
    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
            {
                return [.. _errors];
            }
        }
    }

    /// <inheritdoc />
    public override void Log(ILogMessage message)
    {
        if (message.Level >= NuGet.Common.LogLevel.Error)
        {
            lock (_errors)
            {
                _errors.Add(message.Message);
            }
        }
    }

    /// <inheritdoc />
    public override Task LogAsync(ILogMessage message)
    {
        Log(message);
        return Task.CompletedTask;
    }
}
