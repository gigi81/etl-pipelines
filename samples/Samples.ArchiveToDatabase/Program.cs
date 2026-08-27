using EtlPipelines.Hosting;
using EtlPipelines.Samples.Common;

namespace EtlPipelines.Samples.ArchiveToDatabase;

/// <summary>
/// The sample's command line application.
/// </summary>
/// <remarks>
/// There is no verb of its own here. Everything the sample does - writing the five files, zipping
/// them, unzipping them, creating the tables - is a stage of the pipeline, so the <c>run</c> and
/// <c>list</c> verbs that come with <see cref="EtlPipelinesHost"/> are the whole interface:
/// <code>
/// dotnet run -- list
/// dotnet run -- run archive --work-dir ./out
/// </code>
/// </remarks>
public static class Program
{
    /// <summary>The process entry point.</summary>
    public static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>Runs the application and returns its exit code.</summary>
    public static Task<int> RunAsync(string[] args) =>
        new EtlPipelinesHost("Five CSV files bundled into a zip, then extracted and loaded into five tables.")
            .UseSampleWorkspace(Pipeline.Name, (services, directory) =>
                services.AddPipeline(directory))
            .RunAsync(args);
}
