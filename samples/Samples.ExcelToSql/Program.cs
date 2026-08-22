using EtlPipelines.Hosting;
using EtlPipelines.Samples.Common;

namespace EtlPipelines.Samples.ExcelToSql;

/// <summary>
/// The sample's command line application.
/// </summary>
/// <remarks>
/// There is no verb of its own here. Everything the sample does — putting its input in place included
/// — is a stage of the pipeline, so the <c>run</c> and <c>list</c> verbs that come with
/// <see cref="EtlPipelinesHost"/> are the whole interface:
/// <code>
/// dotnet run -- list
/// dotnet run -- run import --work-dir ./out
/// </code>
/// <see cref="Main"/> is one line over <see cref="RunAsync"/> so that the tests can invoke the sample
/// exactly as a shell does — same verbs, same wiring, same exit code — rather than approximating it.
/// </remarks>
public static class Program
{
    /// <summary>The process entry point.</summary>
    public static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>Runs the application and returns its exit code.</summary>
    public static Task<int> RunAsync(string[] args) =>
        new EtlPipelinesHost("A hand-filled workbook loaded into a table, bad rows set aside.")
            .UseSampleWorkspace(Pipeline.Name, (services, directory) =>
                services.AddPipeline(directory))
            .RunAsync(args);
}
