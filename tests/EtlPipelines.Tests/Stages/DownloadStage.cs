using System.ComponentModel;
using EtlPipelines.Abstractions;
using Microsoft.Extensions.Logging;

namespace EtlPipelines.Tests.Stages;

[Description("Download files from xyz")]
public class DownloadStage : IPipelineStage
{
    public DownloadStage(ILogger<DownloadStage> logger)
    {
        
    }

    public Task Execute()
    {
        throw new NotImplementedException();
    }
}