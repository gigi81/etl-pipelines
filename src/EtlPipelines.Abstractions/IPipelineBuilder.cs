namespace EtlPipelines.Abstractions;

public interface IPipelineBuilder
{
    IPipelineBuilder AddStage<TStage>() where TStage : class, IPipelineStage;
    IPipeline Build();
}