using System.Net;

namespace EtlPipelines.Files.Tests;

/// <summary>A handler that answers every request from a caller-supplied function, so tests never touch a real socket.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    /// <summary>A handler that always answers with <paramref name="content"/> and a 200.</summary>
    public static StubHttpMessageHandler Ok(string content) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
}
