using EtlPipelines.Hosting;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.CsvToDatabase;

/// <summary>
/// The sample's command line application.
/// </summary>
/// <remarks>
/// There is no verb of its own here. Everything the sample does — putting its input in place included
/// — is a stage of the pipeline, so the <c>run</c> and <c>list</c> verbs that come with
/// <see cref="EtlPipelinesHost"/> are the whole interface:
/// <code>
/// dotnet run -- list
/// dotnet run -- run TradesPipeline.Name --work-dir ./out
/// </code>
/// A method rather than the body of <c>Program.cs</c> so that the tests can invoke the sample exactly
/// as a shell does — same verbs, same wiring, same exit code — instead of approximating it.
/// </remarks>
public static class TradesCommand
{
    /// <summary>Runs the application and returns its exit code.</summary>
    public static Task<int> RunAsync(string[] args) =>
        new EtlPipelinesHost("A CSV file loaded into a database table, in one transaction.")
            .UseSampleWorkspace(TradesPipeline.Name, (services, directory) =>
                services.AddTradesPipeline(directory))
            .RunAsync(args);
}
