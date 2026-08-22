using EtlPipelines.Hosting;

namespace EtlPipelines.Samples.Common;

/// <summary>Host wiring every sample shares.</summary>
public static class SampleCommandHost
{
    /// <summary>
    /// Adds the <c>--work-dir</c> option to the root command, where being recursive makes it apply to
    /// every verb the sample has.
    /// </summary>
    /// <remarks>
    /// On the root rather than on each verb's parameters class so that it is declared once, and so
    /// that it can be read from the parse result while services are being registered — before any
    /// verb's parameters object exists.
    /// </remarks>
    public static EtlPipelinesHost UseSampleWorkspace(this EtlPipelinesHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        return host.AddCommands(cli => cli.CommandBuilder.RootCommand.Add(SampleWorkspace.WorkDirOption));
    }
}
