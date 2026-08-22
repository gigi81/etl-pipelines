using Albatross.CommandLine.Annotations;
using EtlPipelines.Hosting.Handlers;

namespace EtlPipelines.Hosting.Commands;

/// <summary>Lists the pipelines the application registered.</summary>
[Verb<ListPipelinesHandler>("list", Description = "Lists the pipelines this application can run.")]
public class ListPipelinesParams
{
}
