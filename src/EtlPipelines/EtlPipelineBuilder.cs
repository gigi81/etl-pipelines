using EtlPipelines.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace EtlPipelines;

public class EtlPipelineBuilder : IPipelineBuilder
{
    private readonly IServiceCollection _services;

    public EtlPipelineBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public IPipelineBuilder AddStage<TStage>() where TStage : class, IPipelineStage
    {
        _services.AddTransient<TStage>();
        return this;
    }

    public IPipeline Build()
    {
        throw new NotImplementedException();
    }
}