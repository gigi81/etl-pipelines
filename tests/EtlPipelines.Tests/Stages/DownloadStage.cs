using System.ComponentModel;
using EtlPipelines.Abstractions;

namespace EtlPipelines.Tests.Stages;

[Description("Download files from xyz")]
public class DownloadStage : IPipelineStage
{
    public DownloadStage(ILogger<DownloadStage> logger)
    {
        
    }
}