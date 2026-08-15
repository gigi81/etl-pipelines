namespace EtlPipelines.Abstractions;

public interface IPipelineStage : IInitializable
{
    Task Execute();
}