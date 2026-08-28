using EtlPipelines.Hosting;

namespace EtlPipelines.Samples.ArchiveToDatabase;

/// <summary>
/// The sample's command line application.
/// </summary>
/// <remarks>
/// Two pipelines, one application: <c>build-feed</c> stands in for the vendor and writes
/// <c>feed.zip</c>; <c>archive</c> is the real job, and only knows how to read one. Run them
/// separately to see that split, or run neither by name and both happen in the order they were
/// registered - setup, then the job that depends on it:
/// <code>
/// dotnet run -- list
/// dotnet run -- run build-feed --work-dir ./out   // just the setup
/// dotnet run -- run archive --work-dir ./out      // just the job (fails first if feed.zip is not there yet)
/// dotnet run -- run --work-dir ./out              // both, in registration order
/// </code>
/// </remarks>
public static class Program
{
    private const string Description = "A vendor's zipped export ('build-feed') extracted and loaded into five tables ('archive').";
    
    /// <summary>The process entry point.</summary>
    public static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>Runs the application and returns its exit code.</summary>
    public static Task<int> RunAsync(string[] args)
    {
        return new EtlPipelinesHost(Description)
            .ConfigureServices((_, services, workspace) =>
                services.AddPipeline(workspace))
            .RunAsync(args);
    }
}
