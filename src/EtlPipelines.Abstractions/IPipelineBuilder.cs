namespace EtlPipelines.Abstractions;

public interface IPipelineBuilder
{
    IPipelineBuilder AddStage<TStage>() where TStage : IPipelineStage;
    IPipeline Build();
}