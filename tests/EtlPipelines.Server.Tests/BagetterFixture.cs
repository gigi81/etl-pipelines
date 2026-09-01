using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace EtlPipelines.Server.Tests;

/// <summary>
/// A genuinely empty <c>bagetter</c> container - no packages pushed to it yet. Built directly via
/// Testcontainers' generic <see cref="ContainerBuilder"/> rather than a purpose-specific package
/// (Testcontainers ships one per database engine already used elsewhere in this repo, but none
/// for bagetter) - the same building block a bare <c>docker run bagetter/bagetter</c> is.
/// </summary>
public sealed class BagetterFixture : IAsyncInitializer, IAsyncDisposable
{
    private const int Port = 8080;

    private readonly IContainer _container = new ContainerBuilder("bagetter/bagetter")
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(Port).ForPath("/v3/index.json")))
        .Build();

    /// <summary>The v3 service index URL - what both <c>NuGetFeed:Url</c> (Server) and <c>dotnet nuget push</c> (test setup) target.</summary>
    public string FeedUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}/v3/index.json";

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
