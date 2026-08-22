using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using EtlPipelines.Abstractions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Samples.Common;

/// <summary>
/// Builds and runs the .NET generic host every sample runs inside.
/// </summary>
/// <remarks>
/// One place, so that each sample's <c>Program.cs</c> is a single line and the pipeline is the only
/// thing in the sample worth reading. Both entry points below build the same host in the same way:
/// the command line gets a temporary directory, a test gets one it chose, and nothing else differs.
/// </remarks>
public static class SampleHost
{
    /// <summary>Runs a sample from a command line and returns a process exit code.</summary>
    public static async Task<int> RunAsync<TSample>(string[] args)
        where TSample : Sample, new()
    {
        var sample = new TSample();
        var workspace = SampleWorkspace.CreateTemporary(sample.PipelineName);

        var (host, state) = await StartAsync(sample, workspace, args).ConfigureAwait(false);

        await DisposeAsync(host).ConfigureAwait(false);

        return state.Failure is null ? 0 : 1;
    }

    /// <summary>
    /// Runs a sample against a directory the caller chose, and hands back the host it ran in.
    /// </summary>
    /// <remarks>
    /// This is the entry point the tests use. They get the same host the command line gets, so what
    /// they prove about a sample is true of the sample as somebody would actually run it — and
    /// keeping the host alive lets a test look at whatever the pipeline registered.
    /// </remarks>
    /// <exception cref="Exception">Whatever the run failed with, rethrown with its stack intact.</exception>
    public static async Task<SampleRun> RunAsync(Sample sample, SampleWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(workspace);

        var (host, state) = await StartAsync(sample, workspace, []).ConfigureAwait(false);

        if (state.Failure is not null)
        {
            await DisposeAsync(host).ConfigureAwait(false);
            ExceptionDispatchInfo.Throw(state.Failure);
        }

        return new SampleRun(host, state.Result!, workspace);
    }

    private static async Task<(IHost Host, SampleRunState State)> StartAsync(
        Sample sample,
        SampleWorkspace workspace,
        string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var state = new SampleRunState();

        // Under ConnectionStrings, so a sample's AddSqliteConnection("sales") finds it through
        // IConfiguration.GetConnectionString exactly as it would in an application with an
        // appsettings.json. Added last, so a value passed on the command line still wins.
        builder.Configuration.AddInMemoryCollection(
            sample.ConnectionStrings(workspace)
                .Select(entry => new KeyValuePair<string, string?>(
                    $"ConnectionStrings:{entry.Key}", entry.Value)));

        // Console only. A sample that also logged to a file or an exporter would be teaching the
        // wrong lesson; the point is that the pipeline reports itself, not where the report goes.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });

        // The host's own "Application started"/"stopping" pair says nothing about the sample.
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        builder.Services.AddSingleton(sample);
        builder.Services.AddSingleton(workspace);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(workspace.FileSystem);

        // Before the runner, so the listener is in place by the time a pipeline starts. Hosted
        // services are started in the order they are registered.
        builder.Services.AddHostedService<PipelineTraceLogging>();
        builder.Services.AddHostedService<SampleRunner>();

        sample.Register(builder.Services, workspace);

        var host = builder.Build();

        // StartAsync and WaitForShutdownAsync rather than RunAsync, which is the two of them plus a
        // dispose. The host has to outlive this method: a caller still has to read the outcome out
        // of it, and disposing would take the container - and everything the pipeline registered in
        // it - down first. Both callers below dispose it when they are done.
        await host.StartAsync().ConfigureAwait(false);

        // The runner stops the application as soon as the pipeline is finished, so this returns
        // rather than sitting waiting for Ctrl+C.
        await host.WaitForShutdownAsync().ConfigureAwait(false);

        return (host, state);
    }

    /// <summary>
    /// <see cref="IHost"/> declares only <see cref="IDisposable"/>, but the implementation the
    /// builder returns also disposes asynchronously - which matters here, because a pipeline's sinks
    /// flush and commit on the way out.
    /// </summary>
    internal static async ValueTask DisposeAsync(IHost host)
    {
        if (host is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        host.Dispose();
    }
}

/// <summary>A finished sample run, and the host it ran in.</summary>
/// <remarks>
/// Disposing this disposes the host, and with it everything the pipeline resolved — so read
/// <see cref="Services"/> before letting it go.
/// </remarks>
public sealed class SampleRun(IHost host, PipelineResult result, SampleWorkspace workspace) : IAsyncDisposable
{
    /// <summary>What the run did.</summary>
    public PipelineResult Result { get; } = result;

    /// <summary>The directory the run wrote into.</summary>
    public SampleWorkspace Workspace { get; } = workspace;

    /// <summary>The container the pipeline ran in.</summary>
    public IServiceProvider Services => host.Services;

    /// <summary>A file in the workspace.</summary>
    public IFileInfo File(string name) => Workspace.File(name);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => SampleHost.DisposeAsync(host);
}
