using EtlPipelines.Tests.Stages;

namespace EtlPipelines.Tests;

public class SamplePipelinesTest
{
    public void Test()
    {
        //arrange
        var pipeline = EtlPipeline.CreateBuilder()
            .AddStage<DownloadStage>()
            .AddStage<TransformStage>()
            .AddStage<UploadStage>()
            .Build();

        //act
        pipeline.Run();
    }
}