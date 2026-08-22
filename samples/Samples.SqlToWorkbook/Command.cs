using EtlPipelines.Hosting;
using EtlPipelines.Samples.Common;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines.Samples.SqlToWorkbook;

/// <summary>
/// The sample's command line application.
/// </summary>
/// <remarks>
/// There is no verb of its own here. Everything the sample does — putting its input in place included
/// — is a stage of the pipeline, so the <c>run</c> and <c>list</c> verbs that come with
/// <see cref="EtlPipelinesHost"/> are the whole interface:
/// <code>
/// dotnet run -- list
/// dotnet run -- run ReportPipeline.Name --work-dir ./out
/// </code>
/// A method rather than the body of <c>Program.cs</c> so that the tests can invoke the sample exactly
/// as a shell does — same verbs, same wiring, same exit code — instead of approximating it.
/// </remarks>
public static class ReportCommand
{
    /// <summary>Runs the application and returns its exit code.</summary>
    public static Task<int> RunAsync(string[] args) =>
        new EtlPipelinesHost("Several queries becoming the sheets of one workbook.")
            .UseSampleWorkspace(ReportPipeline.Name, (services, directory) =>
                services.AddReportPipeline(directory))
            .RunAsync(args);
}
